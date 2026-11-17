using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private readonly List<RegionalReport> _regionalReports = [];

    private void SynchronizeRegional()
    {
        if (Work.Gang is { Phase: not RoamingGangPhase.Complete, Regional: { } order } gang)
        {
            var current = RegionalConflictStore.Operation(database, order.Key);
            if (current is not null && current != order)
            {
                gang = gang with { Revision = checked(gang.Revision + 1), Regional = current };
            }
            if (
                !database.Settings.Capsuleers.RegionalConflicts
                || current?.Status != RegionalOperationStatus.Active
                || Work.ActiveTicks >= order.ExpiresAt
            )
            {
                gang = ReturnGang(gang, "The regional operation ended; returning home.");
            }
            _work = Work with { Gang = gang };
        }
        if (Live?.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work.RegionalEscort is { } assignment && Work.RegionalEscort is null)
        {
            _work = Work with { RegionalEscort = assignment };
        }
        if (
            Work.RegionalEscort is { } escort
            && (
                !database.Settings.Capsuleers.RegionalConflicts
                || escort.ShipId != Pilot.ShipId
                || Work.ActiveTicks >= escort.Operation.ExpiresAt
                || RegionalConflictStore.Operation(database, escort.Operation.Key)?.Status != RegionalOperationStatus.Active
            )
        )
        {
            _work = Work with { RegionalEscort = null };
        }
    }

    private RegionalOperation? PlanRegional(ref CapsuleerTrafficRoute route)
    {
        if (!database.Settings.Capsuleers.RegionalConflicts || Live is not { } live)
        {
            return null;
        }
        var operation = RegionalConflictStore.Plan(_operation.World, _operation.Catalog, live, Pilot.StationId, Work.ActiveTicks);
        if (operation is not null)
        {
            route = new(Pilot.StationId, operation.DestinationStationId, operation.Systems);
        }
        return operation;
    }

    private bool RegionalLaunchBlocked()
    {
        if (!database.Settings.Capsuleers.RegionalConflicts || Live is not { } live)
        {
            return false;
        }
        var home = database.Catalog.Systems[Pilot.SystemId];
        var state = _operation.World.Set<SavedRegionalConflict>().Find(home.RegionId)?.State;
        return state is not null
            && (
                state.Relationships.Any(relation =>
                    relation.LowerGroupId == home.ConstellationId || relation.HigherGroupId == home.ConstellationId
                )
                || state.Operations.Any(operation =>
                    operation.Key.GroupId == home.ConstellationId
                    && operation.Status == RegionalOperationStatus.Active
                    && operation.ExpiresAt > Work.ActiveTicks
                )
            );
    }

    private void LaunchRegional(RoamingGang gang)
    {
        if (gang.Regional is not { } order)
        {
            return;
        }
        RegionalConflictStore.Launch(_operation.World, order, gang);
        if (order.Objective == RegionalObjective.Escort && Live!.HaulerCheckpoints.GetValueOrDefault(order.TargetPilotId) is { } freight)
        {
            _operation.GangEnrollments.Add(
                freight with
                {
                    Work = freight.Work with { RegionalEscort = new(order, freight.Work.ShipId, gang.Members) },
                }
            );
        }
    }

    private bool TryRegionalGrid()
    {
        if (
            Work.Gang is not { Phase: RoamingGangPhase.Hunting, Regional: { } order }
            || order.TargetSystemId != Pilot.SystemId
            || Work.RegionalObjectiveVisited
            || Work.GangReturning
            || order.TargetGridId == 0
            || !Objects.Any(body => body.Id == order.TargetGridId)
        )
        {
            return false;
        }
        _work = Work with
        {
            RegionalObjectiveVisited = true,
            Ratting = new(order.DestinationStationId, [order.TargetGridId], 0, Math.Max(1, order.ExpiresAt - Work.ActiveTicks))
            {
                Patrol = new(PatrolKind.Roam, Pilot.SystemId),
                LastObservedAt = Now,
            },
        };
        FlyToBelt();
        return true;
    }

    private bool LaunchLocalRegional(RoamingGang gang)
    {
        gang = gang with { Revision = checked(gang.Revision + 1), Phase = RoamingGangPhase.Hunting };
        _route = new(gang.HomeStationId, gang.DestinationStationId, gang.Systems);
        _work = Work with
        {
            Gang = gang,
            RouteIndex = 0,
            RouteDirection = 1,
            Journey = new(
                TrafficJourneyKind.Local,
                gang.HomeStationId,
                gang.HomeStationId,
                gang.DestinationStationId,
                gang.Systems,
                gang.Sequence
            ),
        };
        LaunchActivity();
        if (!TryRegionalGrid())
        {
            _work = Work with { Gang = ReturnGang(gang, "The reported grid is no longer available.") };
            BeginTravel();
        }
        return true;
    }

    private void RegionalArrival()
    {
        if (
            Work.Gang?.Regional is { } order
            && Work.RegionalObjectiveVisited
            && Work.TargetId == order.TargetGridId
            && RegionalConflictStore.Operation(database, order.Key) is { ReachedObjective: false }
        )
        {
            RegionalConflictStore.Reached(_operation.World, order.Key);
        }
    }

    private bool WaitForRegionalShipment()
    {
        if (
            Work.Gang is not { Phase: RoamingGangPhase.Hunting, Regional.Objective: RegionalObjective.Escort } gang
            || gang.Regional is not { } order
            || Work.ActiveTicks >= order.ExpiresAt
            || RegionalConflictStore.Operation(database, order.Key)?.Status != RegionalOperationStatus.Active
        )
        {
            return false;
        }
        _work = Work with
        {
            Activity = HaulerActivity.Docked,
            DueTicks = checked(Work.ActiveTicks + TimeSpan.FromSeconds(5).Ticks),
            WaitingReason = "Waiting for the escorted shipment to be delivered.",
        };
        return true;
    }

    private void FinishRegional()
    {
        if (
            Work.Gang is { Phase: RoamingGangPhase.Complete, Regional: { } order } gang
            && RegionalConflictStore.Operation(database, order.Key) is { Status: RegionalOperationStatus.Active } current
        )
        {
            RegionalConflictStore.Finish(
                _operation.World,
                order.Key,
                current.ReachedObjective
                    && order.Objective != RegionalObjective.Escort
                    && gang.Members.All(member => member.Status != RoamingGangMemberStatus.Lost),
                gang.Reason
            );
        }
    }

    private void DeliverRegionalShipment(bool intact)
    {
        if (Work.RegionalEscort is { } escort && Pilot.StationId == escort.Operation.DestinationStationId)
        {
            RegionalConflictStore.Finish(
                _operation.World,
                escort.Operation.Key,
                intact,
                intact ? "The escorted freight was delivered." : "The shipment arrived with missing cargo."
            );
            _work = Work with { RegionalEscort = null };
        }
    }

    private void ObserveRegional(PilotSighting[] sightings, long grid)
    {
        if (
            !database.Settings.Capsuleers.RegionalConflicts
            || Live is not { } live
            || RegionalConflictStore.HomeFor(live, database.Catalog, pilotId) is not { } home
        )
        {
            return;
        }
        foreach (
            var contact in sightings
                .Where(contact => contact.RegionalGroupId is not null && contact.RegionalGroupId != home.GroupId)
                .Take(RoamingGangRules.MaximumMembers)
        )
        {
            var kind =
                contact.Hull == Eden.Gameplay.Ships.HullClass.Hauler ? RegionalReportKind.Freight
                : contact.Hull
                    is Eden.Gameplay.Ships.HullClass.MiningBarge
                        or Eden.Gameplay.Ships.HullClass.Exhumer
                        or Eden.Gameplay.Ships.HullClass.ExpeditionFrigate
                    ? RegionalReportKind.Mining
                : RegionalReportKind.CombatShip;
            var report = new RegionalReport(
                home.GroupId,
                contact.RegionalGroupId,
                contact.PilotId,
                contact.ShipId,
                Pilot.SystemId,
                grid,
                kind,
                Work.ActiveTicks,
                checked(Work.ActiveTicks + RegionalConflictRules.ReportTicks)
            );
            if (RegionalConflictStore.NeedsReport(database, home.SystemId, report))
            {
                _regionalReports.Add(report);
            }
        }
    }

    private void PublishRegionalReports()
    {
        if (Live is not { } live || RegionalConflictStore.HomeFor(live, database.Catalog, pilotId) is not { } home)
        {
            return;
        }
        foreach (var report in _regionalReports)
        {
            if (RegionalConflictStore.NeedsReport(database, home.SystemId, report))
            {
                RegionalConflictStore.Report(_operation.World, _operation.Catalog, home.StationId, report);
            }
        }
    }

    private bool WaitForEscortAtGate(StaticSpaceObject gate)
    {
        if (Work.RegionalEscort is not { } escort || Work.Retreating)
        {
            return false;
        }
        var seen = defense.Observe(Pilot, Work.ActiveTicks).Select(contact => contact.PilotId).ToHashSet();
        if (escort.Guards.Count(guard => Live!.Economy.ActiveShip(guard.PilotId, guard.ShipId)) < RoamingGangRules.MinimumMembers)
        {
            RegionalConflictStore.Finish(_operation.World, escort.Operation.Key, false, "Too few escort ships survived.");
            _work = Work with { RegionalEscort = null };
            return false;
        }
        bool ready = escort
            .Guards.Where(guard => guard.Role != PvpRole.Scout)
            .All(guard =>
                !Live!.Economy.ActiveShip(guard.PilotId, guard.ShipId)
                || Live.HaulerCheckpoints.GetValueOrDefault(guard.PilotId)?.Work.RouteIndex > Work.RouteIndex
                || seen.Contains(guard.PilotId) && Live.HaulerCheckpoints.GetValueOrDefault(guard.PilotId)?.Work.TargetId == gate.Id
            );
        if (ready)
        {
            _work = Work with { GangRendezvousUntil = 0 };
            return false;
        }
        if (Work.GangRendezvousUntil == 0)
        {
            _work = Work with { GangRendezvousUntil = checked(Work.ActiveTicks + RoamingGangRules.RendezvousTicks) };
        }
        if (Work.ActiveTicks >= Work.GangRendezvousUntil)
        {
            RegionalConflictStore.Finish(_operation.World, escort.Operation.Key, false, "The freight escort rendezvous timed out.");
            _work = Work with { RegionalEscort = null };
            return false;
        }
        _navigation.SetAtRest();
        return true;
    }
}
