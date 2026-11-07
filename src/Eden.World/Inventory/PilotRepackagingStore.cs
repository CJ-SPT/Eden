using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Insurance;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

[Injectable]
internal sealed class PilotRepackagingStore(WorldDatabase database) : IPilotRepackaging
{
    public IReadOnlyList<InventoryChange> Repackage(long pilotId, int stationId, IReadOnlyList<RepackageItem> items)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        if (
            pilot.ActiveShip.Position is not null
            || pilot.ActiveShip.StationId != stationId
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
            var ship = world.Ships.Include(s => s.Position).SingleOrDefault(s => s.Id == request.ItemId);
            if (ship is not null)
            {
                if (ship.OwnerId != pilotId || ship.Position is not null || ship.StationId != stationId || ship.StoredInId is not null)
                {
                    throw new InvalidDataException("This ship is unavailable in your station hangar.");
                }
                var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == ship.TypeId);
                var before = PilotFleetStore.Read(catalog, ship);
                var health = world.Set<ShipHealthState>().Find(ship.Id);
                RepackagingRules.Check(
                    new(
                        before,
                        ship.TypeId != FleetDefinitions.CapsuleType && HullCatalog.CanAssemble(catalog, type, type.Group.CategoryId),
                        world.Pilots.Any(p => p.ActiveShipId == ship.Id),
                        world.Set<OwnedItem>().Any(i => i.LocationId == ship.Id) || world.Ships.Any(s => s.StoredInId == ship.Id),
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
                                catalog.Set<ItemType>().Single(t => t.Id == item.TypeId),
                                before.CategoryId
                            );
                RepackagingRules.Check(
                    new(
                        before,
                        supported,
                        false,
                        world.Set<OwnedItem>().Any(i => i.LocationId == item.Id),
                        PilotIndustryStore.IsInstalled(world, item.Id),
                        0,
                        0
                    )
                );
                if (item.Singleton)
                {
                    item.Singleton = false;
                    changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
                }
            }
        }
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }
}
