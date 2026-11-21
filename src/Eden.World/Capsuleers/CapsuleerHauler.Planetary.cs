using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Planetary;
using Eden.Gameplay.Ships;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Market;
using Eden.World.Planetary;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private static readonly TimeSpan PlanetaryRetry = TimeSpan.FromMinutes(5);

    private bool HandlePlanetarySupply(
        EconomyTransaction world,
        CorporationSupplyRun run,
        FactorySupplyShipment shipment,
        FactoryPlanetaryVisit visit
    )
    {
        var job = visit.Production;
        var catalog = _operation.Catalog;
        if (visit.Phase == PlanetaryVisitPhase.Return)
        {
            if (Pilot.StationId != job.FactoryStationId)
            {
                return ContinuePlanetaryFreight(job.FactoryStationId);
            }
            if (Work.Freight is { } freight)
            {
                bool personal = job.Step.TypeId != job.RootProduct || visit.Inputs is not null || Pilot.CorpId != job.CorporationId;
                CapsuleerIndustry.Deliver(world, catalog, Pilot, run, freight, personalWarehouse: personal);
            }
            return CompleteFactorySupply();
        }
        if (visit.Phase is PlanetaryVisitPhase.Warehouse or PlanetaryVisitPhase.DeliverStock)
        {
            if (Pilot.StationId != job.FactoryStationId)
            {
                return ContinuePlanetaryFreight(job.FactoryStationId);
            }
            if (visit.Phase == PlanetaryVisitPhase.DeliverStock)
            {
                CapsuleerIndustry.DeliverPlanetaryStock(world, catalog, Pilot, job);
                return CompleteFactorySupply();
            }
            if (
                CapsuleerIndustry.Employer(world, pilotId, job.CorporationId) is null
                || ammunitionSupply.LoadPlanetaryInputs(world, catalog, Pilot, job) is not { } inputs
            )
            {
                return DeferPlanetarySupply();
            }
            visit = visit with { Phase = PlanetaryVisitPhase.Approach, Inputs = inputs };
            _work = Work with { CorporationSupply = run with { FactorySupply = shipment with { Planetary = visit } } };
        }
        if (Pilot.StationId != job.StagingStationId)
        {
            return ContinuePlanetaryFreight(job.StagingStationId);
        }
        if (!ammunitionSupply.SupplyPlanetaryCenter(world, catalog, Pilot, job, Now))
        {
            ReturnPlanetaryShip(visit, null, null);
            return true;
        }

        PlanetaryOrbitStore.EnsureOffice(world, PlanetaryCatalogReader.Planet(catalog, job.PlanetId));
        _operation.TravelChanged = true;
        var launch = StationUndocking.Launch(Pilot.ShipId, Pilot.Home, Pilot.Flight);
        SqliteWorld.SetPosition(world, catalog, Pilot.ShipId, launch.Position, Pilot.SystemId, Now);
        _pilot = Pilot with { Position = launch.Position };
        _navigation.AdoptHull();
        _navigation.SetVelocity(launch.Velocity);
        RefreshObjects();
        var orbit = visit with { Phase = PlanetaryVisitPhase.Orbit };
        _work = Work with { CorporationSupply = run with { FactorySupply = shipment with { Planetary = orbit } } };
        BeginPlanetaryOrbit(orbit);
        return true;
    }

    private void BeginPlanetaryOrbit(FactoryPlanetaryVisit visit)
    {
        var world = _operation.World;
        var target = CapsuleerIndustry.PlanetaryOffice(world, _operation.Catalog, visit.Production.PlanetId);
        var saved = world.Pilots.Find(pilotId)!;
        CrimewatchPersistence.CheckTravel(world, _operation.Catalog, saved, CrimewatchTravel.Warp, Now);
        if (_warpBlocked)
        {
            throw new InvalidDataException("Warp disruption prevents the customs visit.");
        }
        _target = target;
        _work = Work with
        {
            Activity = HaulerActivity.Flying,
            TargetId = target.Id,
            WaitingReason = null,
            WarpReadyAt = CapsuleerWarpRules.Begin(_navigation, WarpOrder(target), Work.ActiveTicks),
        };
    }

    private void ArriveAtPlanetaryOffice(FactoryPlanetaryVisit visit)
    {
        if (Pilot.Position!.DistanceTo(_target!.Position) - Pilot.Flight.ShipRadius - _target.Radius > WreckDefinitions.TransferRange)
        {
            Follow();
            return;
        }
        var result = ammunitionSupply.VisitPlanet((EconomyTransaction)_operation.World, _operation.Catalog, Pilot, visit, Now);
        ReturnPlanetaryShip(visit, result.Pending, result.Cargo);
    }

    private void ReturnPlanetaryShip(FactoryPlanetaryVisit visit, FactoryPlanetaryProduction? pending, FreightStack? cargo)
    {
        var job = visit.Production;
        var run = Work.CorporationSupply!;
        var shipment = run.FactorySupply!;
        bool success = pending is not null || cargo is not null;
        var returning = visit with { Phase = PlanetaryVisitPhase.Return, Inputs = success ? null : visit.Inputs };
        FreightStack[] stacks =
            cargo is not null ? [cargo]
            : pending is null ? visit.Inputs ?? []
            : [];
        _work = Work with
        {
            CorporationSupply = run with { FactorySupply = shipment with { Planetary = returning } },
            PlanetaryProduction = pending,
            PlanetaryRetryAt = success ? 0 : Now + PlanetaryRetry.Ticks,
            Freight = stacks.Length > 0 ? new(0, job.FactoryStationId, stacks) : null,
        };
        if (Pilot.Position is null && Pilot.StationId == job.FactoryStationId)
        {
            HandlePlanetarySupply((EconomyTransaction)_operation.World, run, shipment, returning);
            return;
        }
        if (!ContinuePlanetaryFreight(job.FactoryStationId) && Pilot.Position is not null)
        {
            throw new InvalidDataException("The planetary shipment has no return dock.");
        }
    }

    private bool ContinuePlanetaryFreight(int destination) =>
        ContinueFactoryFreight(destination, CapsuleerIndustryRules.MaximumPlanetarySupplyJumps);

    private bool ContinueFactoryFreight(int destination, int maximumJumps)
    {
        var route = CapsuleerTrafficRoutes.ToStation(
            _operation.Catalog,
            Pilot.StationId,
            destination,
            (HullClass)Pilot.ShipGroupId,
            AvoidedSupplierSystems(),
            maximumJumps,
            policy: database.Settings.Capsuleers
        );
        if (route is null && Pilot.Position is not null)
        {
            // Dock at the staging station while an unsafe regional route is unavailable.
            route = CapsuleerTrafficRoutes.ToStation(
                _operation.Catalog,
                Pilot.StationId,
                Pilot.StationId,
                (HullClass)Pilot.ShipGroupId,
                policy: database.Settings.Capsuleers
            );
        }
        if (route is null)
        {
            _work = Work with { DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(HaulingRules.PollSeconds).Ticks };
            return true;
        }
        if (Pilot.Position is null)
        {
            BeginSupplyJourney(route);
        }
        else
        {
            _route = route;
            _work = Work with
            {
                Journey = new(
                    TrafficJourneyKind.Regional,
                    route.SourceStationId,
                    route.SourceStationId,
                    route.DestinationStationId,
                    route.Systems,
                    Work.DwellSequence
                ),
                RouteDirection = 1,
                Retreating = false,
                TrafficLane = null,
            };
            BeginTravel();
        }
        return true;
    }

    private bool DeferPlanetarySupply()
    {
        _work = Work with { PlanetaryRetryAt = Now + PlanetaryRetry.Ticks };
        return CompleteFactorySupply();
    }
}
