using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.FactionWarfare;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool OnNullsecExpedition => Work.Journey?.Kind == TrafficJourneyKind.NullsecExpedition;

    private bool TryLaunchNullsecExpedition(ResupplyProgress supplies, FittingCalculation? resupplyFit)
    {
        var policy = database.Settings.Capsuleers;
        bool ratting = policy.BeltRatting;
        bool roaming = policy.RoamingPvp && policy.RoamPercent > 0;
        if (
            !policy.NullsecExpeditions
            || !(ratting || roaming)
            || _operation.World is not EconomyTransaction world
            || Pilot.StationId == 0
            || Pilot.InStructure
            || Pilot.Identity.AllianceId is not null
            || InActiveGang
            || Work.Retreating
            || Work.Freight is { Stacks.Length: > 0 }
            || Work.Resupply?.DestinationStationId is not null
            || Work.CorporationSupply is not null
            || Work.FactorySupply is not null
            || Work.PlanetaryProduction is not null
            || Work.RegionalEscort is not null
            || Work.SovereigntyExpedition is not null
            || Work.HomeDefenseAllianceId is not null
            || Work.MilitiaPatrol is not null
            || Work.Exploration is not null
            || Work.Journey is { } current && Pilot.StationId != current.DestinationStationId
        )
        {
            return false;
        }

        using var phase = SessionPerformance.Phase("traffic.nullsecDeparture");
        long worldSeed = world.Set<SavedCapsuleerProvisioning>().Find(1)?.WorldSeed ?? 0;
        ulong draw = NullsecExpeditionRules.Draw(worldSeed, pilotId, Work.DwellSequence);
        var catalog = _operation.Catalog;
        if (
            !NullsecExpeditionRules.ShouldDepart(draw)
            || !StationCapabilities.Has(catalog, Pilot.OperationId, StationService.Market)
            || !MilitiaEnlistmentStore.CanUseDocking(world, world.Pilots.Find(pilotId)!, Pilot.SystemId, Pilot.ShipGroupId)
            || !CapsuleerWorkHulls.CanLeaveShip(world, catalog, Pilot)
        )
        {
            return false;
        }

        var hull = (HullClass)Pilot.ShipGroupId;
        var avoided = NullsecThreats();
        Func<int, int>? population = Live is { } live ? live.CapsuleerCountInSystem : null;
        if (
            roaming
            && !HasPvpShip
            && (!ratting || (draw & 1) == 0)
            && CapsuleerTrafficRoutes.NullsecExpedition(catalog, Pilot.SystemId, hull, policy, avoided, false, true, draw, population)
                is { } potentialRoam
        )
        {
            int source = Pilot.StationId;
            var route = new CapsuleerTrafficRoute(source, source, potentialRoam.Systems);
            var acquisition = ammunitionSupply.TryBuyEarnedPvpShip(
                world,
                catalog,
                database.Traffic,
                Pilot,
                Now,
                stagingSystem: potentialRoam.Systems[^1],
                visitsHighSecurity: CapsuleerIndustry.PvpRouteVisitsHighSecurity(catalog, route)
            );
            _operation.MarketChanged |= acquisition.MarketChanged;
            if (acquisition.Boarded)
            {
                _pilot = ReadPilot(world, world.Pilots.Find(pilotId)!.AccountId);
                ResetTradeHull();
                hull = (HullClass)Pilot.ShipGroupId;
                long spent = supplies.SpentCents;
                supplies = resupply.Dock(
                    database,
                    world,
                    catalog,
                    pilotId,
                    supplies with
                    {
                        Checked = false,
                        Requirements = [],
                    },
                    Now,
                    out resupplyFit
                );
                _operation.MarketChanged |= supplies.SpentCents != spent;
                _work = Work with { Resupply = supplies };
            }
        }
        var profile = CapsuleerPatrolFit.CombatHull(catalog, Pilot.ShipTypeId)
            ? CapsuleerDefenseProfile.Read(world, catalog, pilotId, resupplyFit)
            : null;
        Eden.Data.World.Inventory.Ship? replacement = null;
        if (
            profile is not { Combat: true }
            || !profile.RattingWeapons.Any(weapon => weapon.AvailableShots > 0)
            || CapsuleerDefenseRules.Retreat(
                PilotHealthStore.Read(world, Pilot.ShipId, ShipHealth.DefenseFrom(profile.ShipAttributes), Now),
                true
            )
        )
        {
            replacement = CapsuleerWorkHulls.Find(world, catalog, Pilot, CapsuleerProfession.Combat, Now);
            if (replacement is null || catalog.FittingType(replacement.TypeId) is not { } type)
            {
                return false;
            }
            profile = CapsuleerDefenseProfile.Read(world, catalog, pilotId, ship: replacement);
            if (!profile.RattingWeapons.Any(weapon => weapon.AvailableShots > 0))
            {
                return false;
            }
            hull = (HullClass)type.GroupId;
        }

        ratting = ratting && !TrafficRules.LightHull(hull) && (replacement is not null || supplies.Requirements.All(need => need.Drone));
        roaming = roaming && CapsuleerIndustry.HasPvpShip(world, pilotId, replacement?.Id ?? Pilot.ShipId);
        if (!(ratting || roaming))
        {
            return false;
        }
        if (
            CapsuleerTrafficRoutes.NullsecExpedition(catalog, Pilot.SystemId, hull, policy, avoided, ratting, roaming, draw, population)
            is not { } destination
        )
        {
            return false;
        }
        if (replacement is not null)
        {
            CapsuleerWorkHulls.Board(world, catalog, Pilot, replacement, Now);
            long account = world.Pilots.Where(value => value.Id == pilotId).Select(value => value.AccountId).Single();
            _pilot = ReadPilot(world, account);
            ResetTradeHull();
        }

        int station = Pilot.StationId;
        _route = new(station, station, destination.Systems);
        _work = Work with
        {
            Journey = new(TrafficJourneyKind.NullsecExpedition, station, station, station, _route.Systems, Work.DwellSequence)
            {
                NullsecWork = destination.Work,
            },
            RouteIndex = 0,
            RouteDirection = 1,
            TrafficLane = null,
        };
        LaunchActivity();
        BeginTravel();
        return true;
    }

    private bool NullsecWorkAllowed(TrafficJourney journey, CapsuleerPopulationPolicy policy) =>
        policy.NullsecExpeditions
        && (
            journey.NullsecWork switch
            {
                NullsecExpeditionWork.Ratting => policy.BeltRatting,
                NullsecExpeditionWork.Roaming => policy.RoamingPvp && policy.RoamPercent > 0,
                _ => false,
            }
        )
        && CapsuleerTrafficRoutes.IsNullsec(database.Catalog, journey.Systems[^1])
        && policy.CanWork(database.Catalog, journey.Systems[^1]);

    private HashSet<int> NullsecThreats() =>
        (Work.Routine?.Threats ?? []).Where(threat => threat.ExpiresAt > Work.ActiveTicks).Select(threat => threat.SystemId).ToHashSet();

    private bool RecallNullsecExpedition()
    {
        if (
            Work.Journey is not { Kind: TrafficJourneyKind.NullsecExpedition } journey
            || Pilot.Position is null
            || Work.RouteDirection < 0
            || Work.Activity == HaulerActivity.Jumping
            || _navigation.Warp is not null
            || NullsecWorkAllowed(journey, database.Settings.Capsuleers)
        )
        {
            return false;
        }
        BeginRetreat();
        return Work.Ratting is null;
    }

    private bool PrepareNullsecLeg(TrafficJourney journey)
    {
        var world = _operation.World;
        var character = world.Pilots.Single(pilot => pilot.Id == pilotId);
        var policy = database.Settings.Capsuleers;
        int index = Array.IndexOf(_route.Systems, Pilot.SystemId);
        bool CanDock(int system) => MilitiaEnlistmentStore.CanUseDocking(world, character, system, Pilot.ShipGroupId);
        bool canReturnToStation =
            database.Catalog.FindStation(journey.SourceStationId) is { } home
            && policy.CanWork(database.Catalog, home.SolarSystemId)
            && CanDock(home.SolarSystemId);
        var avoided = NullsecThreats();
        if (Work.RouteDirection > 0 && canReturnToStation && index >= 0 && NullsecWorkAllowed(journey, policy))
        {
            if (
                index + 1 < _route.Systems.Length
                && NullsecGateTo(_route.Systems[index + 1])
                && !_route.Systems.Skip(index + 1).Any(avoided.Contains)
            )
            {
                return false;
            }
            if (index == _route.Systems.Length - 1 && TryWorkNullsecDestination(journey))
            {
                return true;
            }
        }

        _work = Work with { RouteDirection = -1, Retreating = true };
        bool canContinueHome =
            canReturnToStation
            && (
                index > 0 && NullsecGateTo(_route.Systems[index - 1])
                || index == 0 && Objects.Any(site => site.Id == journey.SourceStationId)
            );
        if (canContinueHome && !_route.Systems.Take(index).Any(avoided.Contains))
        {
            return false;
        }

        var catalog = _operation.Catalog;
        var hull = (HullClass)Pilot.ShipGroupId;
        ulong draw = TravelerRoutineRules.Draw(Work.Routine ?? TravelerRoutineRules.Create(pilotId), journey.Sequence);
        var refuge = CapsuleerTrafficRoutes.NullsecRefuge(catalog, Pilot.SystemId, hull, policy, CanDock, avoided, draw);
        if (refuge is null && canContinueHome)
        {
            return false;
        }
        if (refuge is null && avoided.Count > 0)
        {
            refuge = CapsuleerTrafficRoutes.NullsecRefuge(catalog, Pilot.SystemId, hull, policy, CanDock, new HashSet<int>(), draw);
        }
        if (refuge is null)
        {
            _target = null;
            _navigation.SetAtRest();
            _work = Work with { TargetId = 0 };
            Wait("Waiting for a reachable nullsec refuge.", (int)CapsuleerWorkRegions.HoldInterval.TotalSeconds);
            return true;
        }

        _route = refuge;
        _work = Work with
        {
            Journey = journey with
            {
                SourceStationId = refuge.SourceStationId,
                DestinationStationId = refuge.DestinationStationId,
                Systems = refuge.Systems,
            },
            RouteIndex = refuge.Systems.Length - 1,
        };
        return false;
    }

    private bool NullsecGateTo(int system) => Objects.Any(site => site.DestinationSystemId == system && site.DestinationGateId is not null);

    private bool TryWorkNullsecDestination(TrafficJourney journey)
    {
        if (Pilot.Position is null || Work.Ratting is not null)
        {
            return false;
        }
        bool roam = journey.NullsecWork == NullsecExpeditionWork.Roaming;
        var patrol = roam ? new PatrolProgress(PatrolKind.Roam, Pilot.SystemId) : null;
        if (roam && (!HasPvpShip || !PatrolAllowed(patrol!)))
        {
            return false;
        }

        var grids = PilotAwareness
            .Circuit(
                Objects
                    .Where(site => site.GroupId == AsteroidDefinitions.BeltGroup || roam && site.DestinationGateId is not null)
                    .Select(site => site.Id),
                pilotId,
                journey.Sequence,
                Work.VisitedBelts,
                Work.ActiveTicks
            )
            .Take(roam ? PatrolRules.MaximumGrids : RattingRules.MaximumBelts)
            .ToArray();
        if (grids.Length == 0)
        {
            return false;
        }

        _background = false;
        RefreshObjects();
        _work = Work with
        {
            Ratting = new(journey.SourceStationId, grids, 0, TimeSpan.FromMinutes(RattingRules.MaximumMinutes).Ticks)
            {
                Patrol = patrol,
                LastObservedAt = Now,
            },
        };
        FlyToBelt();
        return true;
    }

    private void ReturnFromNullsecDestination(RattingProgress activity)
    {
        _work = Work with { Ratting = null, RattingLoot = activity.LootIds, RouteDirection = -1, Retreating = true };
        BeginTravel();
    }

    private void FinishNullsecExpedition()
    {
        if (OnNullsecExpedition)
        {
            _work = Work with { Journey = null, Retreating = false };
            _route = CapsuleerTrafficFlight.Route(Founder, _operation.Catalog, null);
            UnloadRattingLoot();
        }
    }
}
