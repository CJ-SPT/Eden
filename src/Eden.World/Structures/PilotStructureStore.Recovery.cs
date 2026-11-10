using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Inventory;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureStore
{
    public StructureResult Withdraw(long pilotId, long structureId, long itemId, int quantity, bool toCargo) =>
        Update(
            pilotId,
            structureId,
            (world, catalog, structure, pilot, _) =>
            {
                var item = world.Set<OwnedItem>().Find(itemId);
                var profile = StructureCatalog.Require(catalog, structure.TypeId);
                if (
                    item is null
                    || item.LocationId != structureId
                    || item.OwnerId != structure.CorporationId
                    || item.TypeId == profile.BuiltinCoreTypeId
                    || item.TypeId == profile.BuiltinModuleTypeId
                    || item.FlagId != StructureRules.FuelBayFlag && structure.State != StructureState.Unanchored
                )
                {
                    throw new InvalidDataException("Only stored fuel or decommissioned contents may be withdrawn.");
                }
                bool docked = pilot.ActiveShip.StructureId == structureId && pilot.ActiveShip.Position is null;
                bool nearby =
                    pilot.ActiveShip.Position is { SystemId: int system } p
                    && system == structure.SystemId
                    && new Eden.Gameplay.Flight.SpacePosition(p.X, p.Y, p.Z).DistanceTo(new(structure.X, structure.Y, structure.Z))
                        <= StructureCatalog.Require(catalog, structure.TypeId).Radius + StructureCatalogRange;
                if (toCargo ? !docked && !nearby : !docked)
                {
                    throw new InvalidDataException("Withdraw into your docked hangar or nearby ship cargo.");
                }
                if (toCargo)
                {
                    double capacity = PilotInventoryStore.HoldCapacity(
                        world,
                        catalog,
                        pilotId,
                        pilot.ActiveShipId,
                        (int)InventoryFlag.Cargo
                    );
                    double used = InventoryPersistence.UsedVolume(world, catalog, pilotId, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
                    if (used + PilotInventoryStore.Volume(catalog, item.TypeId) * quantity > capacity)
                    {
                        throw new InvalidDataException("Your ship does not have enough cargo space.");
                    }
                }
                int type = item.TypeId;
                Consume(world, item, quantity);
                InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    pilotId,
                    toCargo ? pilot.ActiveShipId : structureId,
                    (int)(toCargo ? InventoryFlag.Cargo : InventoryFlag.Hangar),
                    type,
                    quantity
                );
            }
        );

    public StructureResult Recover(long pilotId, long structureId) =>
        Update(
            pilotId,
            structureId,
            (world, catalog, structure, pilot, _) =>
            {
                var profile = StructureCatalog.Require(catalog, structure.TypeId);
                if (
                    structure.State != StructureState.Unanchored
                    || pilot.ActiveShip.Position is not { SystemId: int system } p
                    || system != structure.SystemId
                    || new Eden.Gameplay.Flight.SpacePosition(p.X, p.Y, p.Z).DistanceTo(new(structure.X, structure.Y, structure.Z))
                        > profile.Radius + StructureCatalogRange
                )
                {
                    throw new InvalidDataException("Approach your decommissioned structure in space before recovering it.");
                }
                var contents = world.Set<OwnedItem>().Where(i => i.LocationId == structureId).OrderBy(i => i.Id).ToArray();
                if (contents.Any(i => i.OwnerId != structure.CorporationId) || world.Ships.Any(s => s.StructureId == structureId))
                {
                    throw new InvalidDataException("Clear all personal assets before recovering this structure.");
                }
                bool Builtin(OwnedItem item) => item.TypeId == profile.BuiltinCoreTypeId || item.TypeId == profile.BuiltinModuleTypeId;
                double needed =
                    PilotInventoryStore.Volume(catalog, structure.TypeId)
                    + contents.Where(i => !Builtin(i)).Sum(i => PilotInventoryStore.Volume(catalog, i.TypeId) * i.Quantity);
                double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilotId, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
                double used = InventoryPersistence.UsedVolume(world, catalog, pilotId, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
                if (used + needed > capacity)
                {
                    throw new InvalidDataException("Your ship cannot hold the packaged structure and its remaining contents.");
                }
                world.RemoveRange(world.Set<SavedStructureService>().Where(m => m.StructureId == structureId));
                world.RemoveRange(world.Set<SavedStructureAccess>().Where(a => a.StructureId == structureId));
                foreach (var item in contents)
                {
                    if (Builtin(item))
                    {
                        // Integral equipment returns inside the packaged hull; never grant a second loose copy.
                        world.Remove(item);
                        continue;
                    }
                    item.OwnerId = pilotId;
                    item.LocationId = pilot.ActiveShipId;
                    item.FlagId = (int)InventoryFlag.Cargo;
                    item.Online = false;
                    item.Singleton = false;
                }
                InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    pilotId,
                    pilot.ActiveShipId,
                    (int)InventoryFlag.Cargo,
                    structure.TypeId,
                    1
                );
                world.Remove(structure);
            }
        );
}
