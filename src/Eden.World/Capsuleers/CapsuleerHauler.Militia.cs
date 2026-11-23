using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.World.FactionWarfare;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private const double MilitiaHoldRadius = MilitiaComplexRules.CaptureRange / 2;
    private static readonly long s_militiaPatrolDuration = TimeSpan.FromMinutes(30).Ticks;

    private bool TryLaunchMilitiaPatrol()
    {
        if (_operation.World is not EconomyTransaction world || Live is not { } live)
        {
            return false;
        }
        if (Work.MilitiaPatrol is { Phase: MilitiaPatrolPhase.Staging } staging)
        {
            if (ContinueMilitiaStaging(world, staging))
            {
                return true;
            }
            _work = Work with
            {
                MilitiaPatrol = null,
                Journey = null,
                MilitiaRetryAt = checked(Work.ActiveTicks + s_militiaReviewInterval),
            };
            return false;
        }
        if (
            !database.Settings.Capsuleers.MilitiaPatrols
            || Pilot.InStructure
            || Work.ActiveTicks < Work.MilitiaRetryAt
            || Work.Freight is { Stacks.Length: > 0 }
            || InActiveGang
            || Work
                is not {
                    MilitiaPatrol: null,
                    Ratting: null,
                    Exploration: null,
                    CorporationSupply: null,
                    FactorySupply: null,
                    SovereigntyExpedition: null,
                    HomeDefenseAllianceId: null,
                    RegionalEscort: null,
                    PlanetaryProduction: null,
                    Retreating: false,
                }
            || Work.Journey is { Kind: TrafficJourneyKind.PvpShopping or TrafficJourneyKind.CorporationRelocation }
            || Work.Resupply?.DestinationStationId is not null
        )
        {
            return false;
        }

        _work = Work with { MilitiaRetryAt = checked(Work.ActiveTicks + s_militiaReviewInterval) };
        return TryAssignMilitiaPatrol(live, world);
    }

    private void StartMilitiaFlight(MilitiaPatrolProgress patrol, CapsuleerTrafficRoute route)
    {
        SetMilitiaPatrolRoute(patrol, route);
        _background = false;
        LaunchActivity();
        BeginTravel();
    }

    private void SetMilitiaPatrolRoute(MilitiaPatrolProgress patrol, CapsuleerTrafficRoute route)
    {
        _route = route;
        _work = Work with
        {
            MilitiaPatrol = patrol,
            Journey = new(
                TrafficJourneyKind.MilitiaPatrol,
                patrol.HomeStationId,
                route.SourceStationId,
                route.DestinationStationId,
                route.Systems,
                Work.DwellSequence
            ),
            RouteIndex = 0,
            RouteDirection = 1,
            TrafficLane = null,
            LaneDeparting = false,
            WaitingReason = null,
            MilitiaWaitingReason = null,
        };
    }

    private bool MilitiaPatrolActive(MilitiaPatrolProgress patrol)
    {
        if (
            !database.Settings.Capsuleers.MilitiaPatrols
            || Work.Retreating
            || Work.ActiveTicks >= patrol.ExpiresAt
            || patrol.ShipId != Pilot.ShipId
            || Live is not { } live
            || !database.Settings.Capsuleers.CanWork(database.Catalog, patrol.SystemId)
        )
        {
            return false;
        }
        var affiliation = live.OwnerDirectory.Snapshot.PilotMilitia(pilotId);
        if (affiliation != patrol.Faction && (affiliation is not null || patrol.Phase != MilitiaPatrolPhase.Staging))
        {
            return false;
        }
        return live.Economy.Inspect(world => militia.MilitiaPatrolSiteCurrent(world, patrol.Site, Now));
    }

    private bool StepMilitiaPatrol(MilitiaPatrolProgress patrol)
    {
        if (patrol.ShipId != Pilot.ShipId || Pilot.Position is null && patrol.Phase != MilitiaPatrolPhase.Staging)
        {
            _work = Work with { MilitiaPatrol = null, Journey = null, Retreating = false };
            return false;
        }
        if (Pilot.Position is null || Work.Activity == HaulerActivity.Jumping)
        {
            return false;
        }
        if (Work.Activity == HaulerActivity.Waiting && Work.ActiveTicks < Work.DueTicks)
        {
            return true;
        }
        if (patrol.Phase == MilitiaPatrolPhase.Returning)
        {
            if (Work.Activity == HaulerActivity.Waiting)
            {
                ReturnFromMilitiaPatrol(patrol);
                return true;
            }
            return false;
        }
        if (!MilitiaPatrolActive(patrol))
        {
            if (_navigation.Warp is not null)
            {
                Fly();
            }
            else
            {
                ReturnFromMilitiaPatrol(patrol);
            }
            return true;
        }
        if (patrol.Phase is MilitiaPatrolPhase.Travelling or MilitiaPatrolPhase.Staging)
        {
            return false;
        }
        if (patrol.Phase != MilitiaPatrolPhase.Holding || Pilot.Position.DistanceTo(patrol.Site.Capture.Position) > MilitiaHoldRadius)
        {
            Fly();
        }
        else
        {
            _navigation.SetAtRest();
        }
        return true;
    }

    private static StaticSpaceObject? MilitiaFlightTarget(MilitiaPatrolProgress? patrol) =>
        patrol?.Phase switch
        {
            MilitiaPatrolPhase.ApproachingGate => patrol.Site.Gate,
            // Gate activation returns a point. The scan beacon's display radius is not a warp offset.
            MilitiaPatrolPhase.EnteringSite => patrol.Site.Beacon with { Radius = 0, Solid = false },
            MilitiaPatrolPhase.Holding => patrol.Site.Capture,
            _ => null,
        };

    private void ApproachMilitiaGate(MilitiaPatrolProgress patrol)
    {
        SetMilitiaFlightTarget(patrol with { Phase = MilitiaPatrolPhase.ApproachingGate });
        StartActivityWarp();
    }

    private void SetMilitiaFlightTarget(MilitiaPatrolProgress patrol)
    {
        _target = MilitiaFlightTarget(patrol)!;
        _work = Work with { MilitiaPatrol = patrol, TargetId = _target.Id, Activity = HaulerActivity.Flying };
    }

    private bool ArriveAtMilitiaPatrolTarget(MilitiaPatrolProgress patrol)
    {
        if (patrol.Phase == MilitiaPatrolPhase.Returning)
        {
            var world = _operation.World;
            var station = _operation.Catalog.Definitions.FindStation(Work.DestinationStationId);
            if (
                station is not null
                && station.SolarSystemId == Pilot.SystemId
                && (
                    !CanWorkHere
                    || !MilitiaEnlistmentStore.CanUseDocking(world, world.Pilots.Find(pilotId)!, Pilot.SystemId, Pilot.ShipGroupId)
                )
            )
            {
                ReturnFromMilitiaPatrol(patrol);
                return true;
            }
            return false;
        }
        if (!MilitiaPatrolActive(patrol))
        {
            ReturnFromMilitiaPatrol(patrol);
            return true;
        }
        if (patrol.Phase is MilitiaPatrolPhase.Travelling or MilitiaPatrolPhase.Staging)
        {
            return false;
        }
        if (patrol.Phase == MilitiaPatrolPhase.ApproachingGate)
        {
            var gate = patrol.Site.Gate;
            if (Pilot.Position!.DistanceTo(gate.Position) > MilitiaComplexRules.GateRange + gate.Radius + Pilot.Flight.ShipRadius)
            {
                Follow();
                return true;
            }

            if (
                !militia
                    .MilitiaPatrolSites(_operation.World, _operation.Catalog, pilotId, patrol.SystemId, Now)
                    .Any(site => site.Id == patrol.SiteId)
            )
            {
                ReturnFromMilitiaPatrol(patrol);
                return true;
            }
            var beacon = militia.ActivateMilitiaPatrolGate(_operation.World, _operation.Catalog, pilotId, gate.Id, Now);
            SetMilitiaFlightTarget(
                patrol with
                {
                    Phase = MilitiaPatrolPhase.EnteringSite,
                    Site = patrol.Site with { Beacon = patrol.Site.Beacon with { Position = beacon } },
                }
            );
            StartActivityWarp();
            return true;
        }
        if (patrol.Phase == MilitiaPatrolPhase.EnteringSite)
        {
            if (Pilot.Position!.DistanceTo(patrol.Site.Beacon.Position) > MilitiaHoldRadius)
            {
                Follow();
                return true;
            }
            SetMilitiaFlightTarget(patrol with { Phase = MilitiaPatrolPhase.Holding });
        }

        if (Pilot.Position!.DistanceTo(patrol.Site.Capture.Position) > MilitiaHoldRadius)
        {
            Follow();
        }
        else
        {
            _navigation.SetAtRest();
        }
        return true;
    }

    private void ReturnFromMilitiaPatrol(MilitiaPatrolProgress patrol)
    {
        _work = Work with { Retreating = true };
        if (_navigation.Warp is not null)
        {
            return;
        }
        var world = _operation.World;
        var catalog = _operation.Catalog;
        var pilot = world.Pilots.Find(pilotId)!;
        var definitions = catalog.Definitions;
        var nearby = CapsuleerTrafficRoutes.Nearby(catalog, Pilot.SystemId, (HullClass)Pilot.ShipGroupId, MilitiaRecruitmentJumps);
        var refuge = nearby
            .Systems.Where(entry => world.Settings.Capsuleers.CanWork(definitions, entry.SystemId))
            .Where(entry => MilitiaEnlistmentStore.CanUseDocking(world, pilot, entry.SystemId, Pilot.ShipGroupId))
            .SelectMany(entry => definitions.StationsInSystem(entry.SystemId).Select(station => (entry.Jumps, Station: station)))
            .OrderByDescending(entry => entry.Station.Id == patrol.HomeStationId)
            .ThenBy(entry => entry.Jumps)
            .ThenBy(entry => TravelerRoutineRules.Mix(patrol.RouteDraw ^ (ulong)patrol.SiteId ^ (uint)entry.Station.Id))
            .Select(entry => entry.Station)
            .FirstOrDefault();
        var returning = patrol with { Phase = MilitiaPatrolPhase.Returning };
        if (refuge is null || nearby.PathTo(refuge.SolarSystemId, patrol.RouteDraw ^ (ulong)patrol.SiteId) is not { } path)
        {
            _navigation.SetAtRest();
            _target = null;
            _work = Work with { MilitiaPatrol = returning, TargetId = 0 };
            Wait("Waiting for a reachable militia refuge.");
            return;
        }
        SetMilitiaPatrolRoute(returning, new(patrol.HomeStationId, refuge.Id, path));
        BeginTravel();
    }

    private CapsuleerAttacker? MilitiaProactiveTarget(MilitiaPatrolProgress patrol)
    {
        if (patrol.Phase != MilitiaPatrolPhase.Holding || Pilot.Position is null || _navigation.Warp is not null)
        {
            return null;
        }
        var origin = patrol.Site.Capture.Position;
        if (
            Work.Defense is { TargetId: > 0 } previous
            && defense.PatrolTarget(Pilot, previous.TargetId, origin, Now, militiaOnly: true) is { } current
        )
        {
            return current;
        }
        foreach (var sighting in defense.Observe(Pilot, Work.ActiveTicks).OrderBy(sighting => sighting.Position.DistanceTo(Pilot.Position)))
        {
            if (defense.PatrolTarget(Pilot, sighting.ShipId, origin, Now, militiaOnly: true) is { } enemy)
            {
                return enemy;
            }
        }
        long slot = MilitiaComplexRules.Slot(patrol.SystemId, patrol.Site.Family);
        return patrol.Faction != patrol.Site.Occupier && Live?.Economy.Peek<SavedMilitiaComplex>(slot)?.DefenderId is long defender
            ? ratting.MilitiaDefender(Pilot, defender, Now)
            : null;
    }
}
