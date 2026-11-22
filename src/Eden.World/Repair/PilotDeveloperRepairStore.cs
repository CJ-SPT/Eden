using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Repair;

[Injectable]
internal sealed class PilotDeveloperRepairStore(WorldDatabase database, TimeProvider clock) : IPilotDeveloperRepairs
{
    public DeveloperRepairResult Restore(long pilot, long ship, DeveloperRepairRequest request)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Developer repairs require a running world.");
        using var world = live.Economy.Begin(new(pilots: [pilot], ships: [ship]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var owner = world.Pilots.Find(pilot);
        if (owner is null || owner.ActiveShipId != ship)
        {
            throw new InvalidDataException("Use your current active ship.");
        }
        var hull = owner.ActiveShip;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (request.Kind is DeveloperRepairKind.Ship or DeveloperRepairKind.Capacitor)
        {
            var attributes = PilotFittingStore.ShipAttributes(world, catalog, owner);
            ShipResources.SetCapacitor(world, ship, attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity), now);
            if (request.Kind == DeveloperRepairKind.Ship)
            {
                var health = world.Set<ShipHealthState>().Find(ship);
                if (health is not null)
                {
                    health.ShieldCharge = attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity);
                    health.ArmorDamage = 0;
                    health.StructureDamage = 0;
                    health.UpdatedAt = Math.Max(health.UpdatedAt, now);
                }
                world.RemoveRange(world.Filter<PendingLocalRepair>(repair => repair.ShipId == ship).ToArray());
            }
        }

        OwnedItem[] modules = request.Kind switch
        {
            DeveloperRepairKind.OwnedModule when request.Item is long itemId => [OwnedModule(itemId)],
            DeveloperRepairKind.Ship or DeveloperRepairKind.FittedModules => world.FittedItems(pilot, ship),
            _ => Array.Empty<OwnedItem>(),
        };
        List<ModuleRepair> repaired = [];
        List<MagazineChange> charges = [];
        foreach (var module in modules)
        {
            if (world.Set<SavedModuleCondition>().Find(module.Id) is { Damage: > 0 } condition)
            {
                double damage = condition.Damage;
                var restore = ModuleConditionPersistence.Repair(world, catalog, module, damage);
                repaired.Add(new(module.Id, module.TypeId, damage - restore.Damage, restore.Damage));
            }
            if (
                request.Kind == DeveloperRepairKind.Ship
                && PilotAmmunitionStore.Contents(world, module.Id) is { CrystalDamage: > 0 } crystal
            )
            {
                var before = PilotAmmunitionStore.Read(world, module, crystal);
                crystal.CrystalDamage = 0;
                charges.Add(new(before, before with { Damage = 0 }));
            }
        }
        world.Commit();
        return new(repaired, charges);

        OwnedItem OwnedModule(long itemId)
        {
            if (
                world.Set<OwnedItem>().Find(itemId) is { OwnerId: var ownerId } item
                && ownerId == pilot
                && (item.LocationId == ship || hull.Position is null && item.LocationId == hull.DockedLocationId)
                && catalog.RequireItem(item.TypeId).CategoryId == (int)InventoryCategory.Module
                && item.Singleton
                && item.Quantity == 1
            )
            {
                return item;
            }
            throw new InvalidDataException("Choose an owned module on your ship or in your docked hangar.");
        }
    }
}
