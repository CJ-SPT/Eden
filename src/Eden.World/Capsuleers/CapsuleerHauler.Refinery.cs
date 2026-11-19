using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool HandleRefinerySupply(
        EconomyTransaction world,
        CorporationSupplyRun run,
        FactorySupplyShipment shipment,
        FactoryRefineryVisit visit
    )
    {
        var task = visit.Task;
        var catalog = _operation.Catalog;
        if (visit.Phase == RefineryVisitPhase.Return)
        {
            if (Pilot.StationId != task.FactoryStationId)
            {
                return ContinueRefineryFreight(task.FactoryStationId);
            }
            if (Work.Freight is { } returns)
            {
                CapsuleerIndustry.Deliver(world, catalog, Pilot, run, returns);
            }
            return CompleteFactorySupply();
        }
        if (visit.Phase == RefineryVisitPhase.Warehouse)
        {
            if (Pilot.StationId != task.FactoryStationId)
            {
                return ContinueRefineryFreight(task.FactoryStationId);
            }
            if (!CapsuleerIndustry.LoadRefineryCargo(world, catalog, Pilot, run, task, Now, out var cargo))
            {
                return CompleteFactorySupply();
            }
            visit = visit with { Phase = RefineryVisitPhase.Approach, Cargo = cargo };
            _work = Work with { CorporationSupply = run with { FactorySupply = shipment with { Refinery = visit } } };
        }
        if (Pilot.StationId != task.StagingStationId)
        {
            return ContinueRefineryFreight(task.StagingStationId);
        }
        var launch = StationUndocking.Launch(Pilot.ShipId, Pilot.Home, Pilot.Flight);
        SqliteWorld.SetPosition(world, catalog, Pilot.ShipId, launch.Position, Pilot.SystemId, Now);
        _operation.TravelChanged = true;
        _pilot = Pilot with { Position = launch.Position };
        _navigation.AdoptHull();
        _navigation.SetVelocity(launch.Velocity);
        RefreshObjects();
        var onGrid = visit with { Phase = RefineryVisitPhase.OnGrid };
        _work = Work with { CorporationSupply = run with { FactorySupply = shipment with { Refinery = onGrid } } };
        BeginRefineryApproach(onGrid);
        return true;
    }

    private void BeginRefineryApproach(FactoryRefineryVisit visit)
    {
        var world = _operation.World;
        var target = CapsuleerIndustry.RefineryTarget(world, _operation.Catalog, visit.Task);
        CrimewatchPersistence.CheckTravel(world, _operation.Catalog, world.Pilots.Find(pilotId)!, CrimewatchTravel.Warp, Now);
        if (_warpBlocked)
        {
            throw new InvalidDataException("Warp disruption prevents the refinery visit.");
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

    private void ArriveAtRefinery(FactoryRefineryVisit visit)
    {
        if (Pilot.Position!.DistanceTo(_target!.Position) > MoonMiningRules.PlacementRange)
        {
            Follow();
            return;
        }
        var world = (EconomyTransaction)_operation.World;
        var run = Work.CorporationSupply!;
        var task = visit.Task;
        CapsuleerIndustry.PerformRefineryWork(world, _operation.Catalog, Pilot, run, visit, Now);
        world.SaveChanges();
        FreightManifest? returns =
            visit.Cargo is { } stack
            && world.Set<OwnedItem>().Find(stack.ItemId) is { } unused
            && unused.OwnerId == pilotId
            && unused.LocationId == Pilot.ShipId
            && unused.FlagId == (int)InventoryFlag.Cargo
                ? new(0, task.FactoryStationId, [stack with { Quantity = Math.Min(stack.Quantity, unused.Quantity) }])
                : null;
        var visitHome = visit with { Phase = RefineryVisitPhase.Return, Cargo = null };
        _work = Work with
        {
            CorporationSupply = run with { FactorySupply = run.FactorySupply! with { Refinery = visitHome } },
            Freight = returns,
        };
        _operation.TravelChanged = true;
        ContinueRefineryFreight(task.FactoryStationId);
    }

    private bool ContinueRefineryFreight(int destination) =>
        ContinueFactoryFreight(destination, CapsuleerIndustryRules.MaximumRefinerySupplyJumps);
}
