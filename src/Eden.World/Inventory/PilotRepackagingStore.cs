using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Insurance;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Inventory;

[Injectable]
internal sealed class PilotRepackagingStore(WorldDatabase database) : IPilotRepackaging
{
    public IReadOnlyList<InventoryChange> Repackage(long pilotId, long stationId, IReadOnlyList<RepackageItem> items)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: items.Select(item => item.ItemId)));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Filter<Pilot>(p => p.Id == pilotId).Single();
        if (
            pilot.ActiveShip.Position is not null
            || pilot.ActiveShip.DockedLocationId != stationId
            || items.Count == 0
            || items.Select(i => i.ItemId).Distinct().Count() != items.Count
            || items.Any(i => i.LocationId != stationId)
        )
        {
            throw new InvalidDataException("Repackage distinct owned items at your current docked station.");
        }
        List<InventoryChange> changes = [];
        foreach (var request in items)
        {
            var ship = world.Ships.Find(request.ItemId);
            if (ship is not null)
            {
                if (
                    ship.OwnerId != pilotId
                    || ship.Position is not null
                    || ship.DockedLocationId != stationId
                    || ship.StoredInId is not null
                )
                {
                    throw new InvalidDataException("This ship is unavailable in your station hangar.");
                }
                var type = catalog.FittingType(ship.TypeId) ?? throw new InvalidDataException("Unknown ship type.");
                var before = PilotFleetStore.Read(catalog, ship);
                var health = world.Set<ShipHealthState>().Find(ship.Id);
                RepackagingRules.Check(
                    new(
                        before,
                        ship.TypeId != FleetDefinitions.CapsuleType && HullCatalog.CanAssemble(catalog, type, type.CategoryId),
                        world.Filter<Pilot>(p => p.ActiveShipId == ship.Id).Any(),
                        world.Filter<OwnedItem>(i => i.LocationId == ship.Id).Any()
                            || world.Filter<Ship>(s => s.StoredInId == ship.Id).Any(),
                        false,
                        health?.ArmorDamage ?? 0,
                        health?.StructureDamage ?? 0
                    )
                );
                PilotInsuranceStore.VoidRemovedHull(world, ship.Id);
                world.Remove(ship);
                var packaged = world
                    .Add(
                        new OwnedItem
                        {
                            Id = ship.Id,
                            OwnerId = pilotId,
                            TypeId = ship.TypeId,
                            LocationId = stationId,
                            FlagId = (int)InventoryFlag.Hangar,
                            Quantity = 1,
                        }
                    )
                    .Entity;
                changes.Add(new(PilotInventoryStore.Read(catalog, packaged), before));
            }
            else
            {
                var item = InventoryPersistence.OwnedStack(world, pilotId, request.ItemId);
                if (item is null || item.LocationId != stationId || item.FlagId != (int)InventoryFlag.Hangar)
                {
                    throw new InvalidDataException("This item is unavailable in your station hangar.");
                }
                var before = PilotInventoryStore.Read(catalog, item);
                bool supported =
                    before.CategoryId == (int)InventoryCategory.Module
                        ? FittingCatalog.Definition(catalog, item.TypeId) is not null
                        : before.CategoryId == (int)InventoryCategory.Ship
                            && HullCatalog.CanAssemble(
                                catalog,
                                catalog.FittingType(item.TypeId) ?? throw new InvalidDataException("Unknown ship type."),
                                before.CategoryId
                            );
                RepackagingRules.Check(
                    new(
                        before,
                        supported,
                        false,
                        world.Filter<OwnedItem>(i => i.LocationId == item.Id).Any(),
                        PilotIndustryStore.IsInstalled(world, item.Id),
                        0,
                        world.Set<SavedModuleCondition>().Find(item.Id)?.Damage ?? 0
                    )
                );
                if (item.Singleton)
                {
                    item.Singleton = false;
                    changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
                }
            }
        }
        transaction.Commit();
        return changes;
    }
}
