using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Structures;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerMiner
{
    private StaticSpaceObject? FactoryTarget(WorldAction world, CatalogDbContext catalog, long id) =>
        world.Set<SavedStructure>().Find(id) is { } structure && CapsuleerIndustry.CanDockAtFactory(world, catalog, Pilot, structure)
            ? FactoryContact(catalog, structure)
            : null;

    private static StaticSpaceObject FactoryContact(CatalogDbContext catalog, SavedStructure structure)
    {
        var profile = StructureCatalog.Require(catalog, structure.TypeId);
        return new(
            structure.Id,
            structure.TypeId,
            profile.GroupId,
            profile.CategoryId,
            structure.CorporationId,
            new(structure.X, structure.Y, structure.Z),
            (float)profile.Radius
        )
        {
            Name = structure.Name,
            Collision = StaticCollisionCatalog.Read(catalog, structure.TypeId)?.Rotate(structure.Yaw),
        };
    }

    private bool TryStageFactory(EconomyTransaction world)
    {
        var catalog = _operation.Catalog;
        if (industry.PlanFactoryStaging(world, catalog, Pilot, Now) is not { } plan)
        {
            return false;
        }
        const int cargoFlag = (int)InventoryFlag.Cargo;
        Dictionary<long, int> loaded = [];
        foreach (var stack in plan.Load)
        {
            var changes = PilotInventoryStore.Move(
                world,
                catalog,
                pilotId,
                Pilot.ShipId,
                new([stack.ItemId], Pilot.StationId, cargoFlag, stack.Quantity),
                Now
            );
            var aboard = changes.Single(change => change.Item.LocationId == Pilot.ShipId && change.Item.Quantity > 0).Item;
            loaded[aboard.Id] = checked(loaded.GetValueOrDefault(aboard.Id) + stack.Quantity);
        }
        _work = Work with
        {
            FactoryStage = new(
                plan.Structure.Id,
                Pilot.StationId,
                loaded.Select(stack => new CapsuleerFactoryCargo(stack.Key, stack.Value)).ToArray()
            ),
            FactorySupply = null,
        };
        UndockMiner();
        StartCourse(FactoryContact(catalog, plan.Structure), MinerActivity.Returning);
        return true;
    }

    private void UnloadFactoryCargo(EconomyTransaction world)
    {
        if (Work.FactoryStage is not { } stage)
        {
            return;
        }
        foreach (var stack in stage.Cargo)
        {
            if (
                world.Set<OwnedItem>().Find(stack.ItemId) is { FlagId: (int)InventoryFlag.Cargo } item
                && item.OwnerId == pilotId
                && item.LocationId == Pilot.ShipId
            )
            {
                PilotInventoryStore.Move(
                    world,
                    _operation.Catalog,
                    pilotId,
                    Pilot.DockedLocationId,
                    new([stack.ItemId], Pilot.ShipId, (int)InventoryFlag.Hangar, Math.Min(stack.Quantity, item.Quantity)),
                    Now
                );
            }
        }
        _work = Work with { FactoryStage = Pilot.InStructure ? stage with { Cargo = [] } : null };
    }

    private bool LeaveUnavailableFactory()
    {
        if (!Pilot.InStructure)
        {
            return false;
        }
        var world = _operation.World;
        var catalog = _operation.Catalog;
        var ship = world.Ships.Find(Pilot.ShipId)!;
        if (ship.DockedLocationId != Pilot.DockedLocationId)
        {
            _pilot = ships.FindPilot(world, CapsuleerCommunityStore.MinerAccountId, pilotId)!;
            RefreshObjects();
            return false;
        }
        var stage = Work.FactoryStage ?? new(Pilot.DockedLocationId, ship.StationId, []);
        _work = Work with { FactoryStage = stage };
        if (Pilot.ShipGroupId != FleetDefinitions.CapsuleGroup && FactoryTarget(world, catalog, Pilot.DockedLocationId) is not null)
        {
            return false;
        }
        _work = Work with { FactoryStage = stage with { StructureId = 0 } };
        UndockMiner();
        ReturnHome();
        return true;
    }
}
