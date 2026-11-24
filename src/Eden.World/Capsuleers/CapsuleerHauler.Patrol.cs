using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool CanStartLocalPvp => !_background || Live is not null && CapsuleerTrafficRoutes.IsNullsec(database.Catalog, Pilot.SystemId);

    private bool TryLaunchPatrol(long sequence, bool intentional = false)
    {
        var policy = database.Settings.Capsuleers;
        var patrol = new PatrolProgress(PatrolKind.Roam, Pilot.SystemId);
        if (!PatrolAllowed(patrol) || !intentional && !PatrolRules.Choose(pilotId, sequence, policy.RoamPercent))
        {
            return false;
        }
        var grids = Objects
            .Where(o => o.DestinationGateId is not null || o.GroupId == AsteroidDefinitions.BeltGroup)
            .OrderBy(o => TravelerRoutineRules.Mix((ulong)o.Id ^ (ulong)pilotId ^ (ulong)sequence))
            .Take(PatrolRules.MaximumGrids)
            .Select(o => o.Id)
            .ToArray();
        if (grids.Length == 0)
        {
            return false;
        }
        LaunchPatrol(patrol, grids, TimeSpan.FromMinutes(RattingRules.MaximumMinutes).Ticks);
        return true;
    }

    private bool GangContactBeyondGate(StaticSpaceObject gate) =>
        Work.Gang is { Phase: RoamingGangPhase.Hunting } gang
        && gate is { DestinationSystemId: { } system, DestinationGateId: { } destination }
        && RoamingGangRules.Reported(gang, system, destination, Work.ActiveTicks) != 0;

    private bool TryPatrolTransitGate(StaticSpaceObject gate)
    {
        var policy = database.Settings.Capsuleers;
        bool expedition = Work.Journey is { Kind: TrafficJourneyKind.NullsecExpedition, NullsecWork: NullsecExpeditionWork.Roaming };
        if (
            _background && !expedition
            || !policy.RoamingPvp
            || !HasPvpShip
            || OnNullsecExpedition && !expedition
            || Work.Journey?.Kind == TrafficJourneyKind.PvpShopping
            || Work.Retreating
            || Work.Freight?.Stacks.Length > 0
            || Work.Resupply?.DestinationStationId is not null
            || Work.LastPatrolledGateId == gate.Id
            || GangContactBeyondGate(gate)
            || !InActiveGang
                && !expedition
                && _operation.World is Eden.World.Runtime.Economy.EconomyTransaction world
                && DynamicWorkPilot(world)
            || !InActiveGang && !PatrolRules.Choose(pilotId, gate.Id ^ (Work.Journey?.Sequence ?? Work.DwellSequence), policy.RoamPercent)
        )
        {
            return false;
        }
        var patrol = new PatrolProgress(PatrolKind.Transit, Pilot.SystemId);
        if (!PatrolAllowed(patrol))
        {
            return false;
        }
        var fit = defense.Profile(pilotId);
        if (
            !fit.Combat
            || !(fit.HasDamage || InActiveGang && fit.HasSupport)
            || health.Read(pilotId, Now) is not { } current
            || CapsuleerDefenseRules.Retreat(current, true)
        )
        {
            return false;
        }
        _navigation.SetAtRest();
        _work = Work with
        {
            LastPatrolledGateId = gate.Id,
            Ratting = new(Work.DestinationStationId, [gate.Id], 0, TimeSpan.FromMinutes(RattingRules.MaximumMinutes).Ticks)
            {
                Phase = RattingPhase.Searching,
                Patrol = patrol,
                LastObservedAt = Now,
            },
        };
        return true;
    }

    private void LaunchPatrol(PatrolProgress patrol, long[] grids, long duration)
    {
        _work = Work with
        {
            Ratting = new(Pilot.StationId, grids, 0, duration) { Patrol = patrol, LastObservedAt = Now },
        };
        LaunchActivity();
        FlyToBelt();
        // Launch needs only static geometry. The first flight turn prepares the active patrol's resource geometry.
        _background = false;
    }

    private bool PatrolAllowed(PatrolProgress patrol) =>
        Pilot.SystemId == patrol.SystemId
        && PatrolRules.Allowed(
            patrol,
            database.Catalog.Definition(CapsuleerProvisioningCatalog.Security, Pilot.SystemId),
            database.Settings.Capsuleers
        );

    private CapsuleerAttacker? PatrolTarget(RattingProgress activity, PatrolProgress patrol)
    {
        if (patrol.Camp is { } camp && activity.Phase != RattingPhase.Returning)
        {
            return CampTarget(activity, patrol, camp);
        }
        if (activity.RemainingTicks <= 0 || !PatrolAllowed(patrol) || Work.Retreating)
        {
            EndSortie(activity);
            return null;
        }
        if (activity.Phase is not (RattingPhase.Searching or RattingPhase.Fighting) || _navigation.Warp is not null || _target is null)
        {
            return null;
        }
        if (Work.ActiveTicks < patrol.NextLook)
        {
            return activity.TargetId == 0 ? null : defense.PatrolTarget(Pilot, activity.TargetId, _target.Position, Now);
        }
        patrol = patrol with
        {
            NextLook = Work.ActiveTicks + TimeSpan.FromSeconds(PilotAwareness.LookSeconds(pilotId)).Ticks,
            LeaveGridAt =
                patrol.LeaveGridAt > 0
                    ? patrol.LeaveGridAt
                    : PatrolRules.GridDeparture(pilotId, Pilot.SystemId, _target.Id, Work.ActiveTicks, Work.Gang),
        };
        var seen = defense.Observe(Pilot, Work.ActiveTicks);
        ObserveRegional(seen, _target.Id);
        var gang = InActiveGang ? Work.Gang : null;
        int? regionalGroup =
            database.Settings.Capsuleers.RegionalConflicts && Live is { } live
                ? RegionalConflictStore.GroupForPilot(live, database.Catalog, pilotId)
                : null;
        bool Member(long other) => gang?.Members.Any(member => member.PilotId == other) == true;
        var candidates = PatrolRules
            .Candidates(Pilot, _target.Position, seen)
            .Where(contact =>
                !Member(contact.PilotId)
                && !PatrolRules.Friendly(Pilot, contact, regionalGroup)
                && (gang?.Regional is not { } order || RegionalConflictStore.CanTarget(database, order, contact, Work.ActiveTicks))
            )
            .Where(contact => defense.PatrolTarget(Pilot, contact.ShipId, _target.Position, Now) is not null)
            .ToArray();
        var profile = defense.Profile(pilotId);
        double friendly =
            PvpTactics.Threat((Eden.Gameplay.Ships.HullClass)Pilot.ShipGroupId)
            + seen.Where(contact =>
                    (Member(contact.PilotId) || PatrolRules.Friendly(Pilot, contact, regionalGroup))
                    && contact.Position.DistanceTo(_target.Position) <= PatrolRules.PursuitMeters
                )
                .Sum(contact => PvpTactics.Threat(contact.Hull));
        long reported = gang is null ? 0 : RoamingGangRules.Reported(gang, Pilot.SystemId, _target.Id, Work.ActiveTicks);
        double opposition = candidates.Sum(contact => PvpTactics.Threat(contact.Hull));
        bool reportingOnly = false;
        var member = gang is null ? null : GangMember;
        bool scout = member?.Role == PvpRole.Scout;
        PvpDecision decision;
        if (scout && candidates.Length > 0)
        {
            var contacts = candidates
                .OrderByDescending(contact => contact.ShipId == reported)
                .ThenByDescending(contact => PvpTactics.Threat(contact.Hull))
                .ThenBy(contact => contact.ShipId)
                .ToArray();
            var engage = contacts.FirstOrDefault(contact =>
                RoamingGangRules.CanScoutTackle(
                    gang!,
                    member!,
                    Pilot.Position!,
                    Pilot.Flight.ShipRadius,
                    Pilot.Flight.MaxSpeed,
                    Math.Min(profile.TackleRange, profile.TargetRange),
                    contact,
                    seen,
                    opposition
                )
            );
            reportingOnly = engage is null;
            decision = new(
                (engage ?? contacts[0]).ShipId,
                0,
                reportingOnly ? "Scouting a visible contact for the gang." : "Tackling with nearby gang support."
            );
        }
        else
        {
            decision = PvpTactics.Select(
                Pilot.Position!,
                _navigation.Velocity,
                profile.RattingWeapons,
                candidates,
                friendly,
                activity.TargetId,
                patrol.CommittedAt,
                Work.ActiveTicks,
                reported,
                PilotAwareness.Cautious(pilotId),
                Pilot.Flight.MaxSpeed,
                Pilot.Flight.ShipRadius,
                profile.HasSupport ? profile.SupportRange : 0
            );
        }
        var target = decision.TargetId == 0 ? null : defense.PatrolTarget(Pilot, decision.TargetId, _target.Position, Now);
        patrol = patrol with
        {
            CommittedAt = decision.TargetId != activity.TargetId ? Work.ActiveTicks : patrol.CommittedAt,
            DecisionReason = decision.Reason,
            WatchingForGang = scout && profile.TackleRange > 0 && target is not null,
        };
        if (gang is not null && target is not null)
        {
            if (scout || gang.LeaderId == pilotId || reported == 0)
            {
                _work = Work with { Gang = RoamingGangRules.Report(gang, target.ShipId, Pilot.SystemId, _target.Id, Work.ActiveTicks) };
            }
            if (reportingOnly)
            {
                target = null;
            }
        }
        double gangStrength =
            gang?.Members.Where(member => member.Status == RoamingGangMemberStatus.Active).Sum(member => PvpTactics.Threat(member.Hull))
            ?? friendly;
        if (gang is not null && opposition > gangStrength * 2)
        {
            _work = Work with { Gang = ReturnGang(Work.Gang!, decision.Reason) };
        }
        _work = Work with
        {
            Ratting = activity with
            {
                Patrol = patrol,
                TargetId = target?.ShipId ?? 0,
                Phase = target is null ? RattingPhase.Searching : RattingPhase.Fighting,
            },
        };
        return target;
    }
}
