using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.World.FactionWarfare;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private const double MilitiaHoldRadius = MilitiaComplexRules.CaptureRange / 2;
    private const int MilitiaRefugeJumps = 2;
    private static readonly long s_militiaPatrolDuration = TimeSpan.FromMinutes(30).Ticks;

    private bool TryLaunchMilitiaPatrol()
    {
        if (
            !database.Settings.Capsuleers.MilitiaPatrols
            || MilitiaPatrolRules.StagingFaction(Pilot.StationId) is not { } faction
            || Live is not { } live
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
            || live.CapsuleerParticipants(new(systems: [MilitiaPatrolRules.Aldranette, MilitiaPatrolRules.Nennamaila]))
                .Count(pilot => live.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work.MilitiaPatrol?.Faction == faction)
                >= MilitiaPatrolRules.MaximumPerFaction
            || !TryPrepareMilitiaPatrol()
        )
        {
            return false;
        }

        var world = _operation.World;
        var catalog = _operation.Catalog;
        var site = militia.MilitiaPatrolSites(world, catalog, pilotId, MilitiaPatrolRules.Aldranette, Now).FirstOrDefault();
        if (site is null || FactionWarzoneStore.System(world, site.SystemId)?.Adjacency != WarzoneAdjacency.Frontline)
        {
            return false;
        }

        var route = CapsuleerTrafficRoutes.ToStation(
            catalog,
            Pilot.StationId,
            MilitiaPatrolRules.AldranetteStation,
            (HullClass)Pilot.ShipGroupId,
            maximumJumps: 1,
            policy: database.Settings.Capsuleers
        );
        if (route is null)
        {
            return false;
        }

        var patrol = new MilitiaPatrolProgress(
            Pilot.ShipId,
            faction,
            Pilot.StationId,
            site,
            MilitiaPatrolPhase.Travelling,
            checked(Work.ActiveTicks + s_militiaPatrolDuration)
        );
        SetMilitiaPatrolRoute(patrol, route);
        _background = false;
        LaunchActivity();
        BeginTravel();
        return true;
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
            || live.OwnerDirectory.Snapshot.PilotMilitia(pilotId) != patrol.Faction
        )
        {
            return false;
        }
        return live.Economy.Inspect(world => militia.MilitiaPatrolSiteCurrent(world, patrol.Site, Now));
    }

    private bool StepMilitiaPatrol(MilitiaPatrolProgress patrol)
    {
        if (patrol.ShipId != Pilot.ShipId || Pilot.Position is null)
        {
            _work = Work with { MilitiaPatrol = null, Journey = null, Retreating = false };
            return false;
        }
        if (Work.Activity == HaulerActivity.Jumping)
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
        if (!MilitiaPatrolActive(patrol) || !CanWorkHere)
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
        if (patrol.Phase == MilitiaPatrolPhase.Travelling)
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
            return false;
        }
        if (!MilitiaPatrolActive(patrol))
        {
            ReturnFromMilitiaPatrol(patrol);
            return true;
        }
        if (patrol.Phase == MilitiaPatrolPhase.Travelling)
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
        var nearby = definitions.GatesInSystem(Pilot.SystemId).Select(gate => gate.DestinationSystemId).Prepend(Pilot.SystemId);
        var source = definitions.StationsInSystem(Pilot.SystemId).FirstOrDefault();
        var route = source is null
            ? null
            : nearby
                .SelectMany(system => definitions.StationsInSystem(system))
                .Where(station => MilitiaEnlistmentStore.CanUseDocking(world, pilot, station.SolarSystemId, Pilot.ShipGroupId))
                .OrderByDescending(station => station.Id == patrol.HomeStationId)
                .ThenByDescending(station => station.SolarSystemId == Pilot.SystemId)
                .ThenBy(station => station.Id)
                .Select(station =>
                    CapsuleerTrafficRoutes.ToStation(
                        catalog,
                        source.Id,
                        station.Id,
                        (HullClass)Pilot.ShipGroupId,
                        maximumJumps: MilitiaRefugeJumps
                    )
                )
                .OfType<CapsuleerTrafficRoute>()
                .FirstOrDefault();
        var returning = patrol with { Phase = MilitiaPatrolPhase.Returning };
        if (route is null)
        {
            _navigation.SetAtRest();
            _target = null;
            _work = Work with { MilitiaPatrol = returning, TargetId = 0 };
            Wait("Waiting for a reachable militia refuge.");
            return;
        }
        SetMilitiaPatrolRoute(returning, route);
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
