using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

[Injectable]
internal sealed class PilotShieldBoostStore(WorldDatabase database) : IPilotShieldBoosting
{
    public IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = ModuleAttributes(world, catalog, pilotId, module);
        transaction.Commit();
        return result;
    }

    internal static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        InventoryItem module
    ) => PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, module);

    public ShieldBoost Boost(long pilotId, long moduleId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var module = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot.ActiveShip.Position is null
            || module is null
            || module.OwnerId != pilotId
            || module.LocationId != pilot.ActiveShipId
            || !module.Singleton
            || !module.Online
            || !InventoryDefinitions.IsMediumSlot(module.FlagId)
            || catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId != ShieldBoostDefinitions.Group
            || FittingCatalog.Definition(catalog, module.TypeId) is null
        )
        {
            throw new InvalidDataException("Use an online fitted shield booster on your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        var attributes = ModuleAttributes(world, catalog, pilotId, PilotInventoryStore.Read(catalog, module));
        ShieldBooster booster = new(
            moduleId,
            module.TypeId,
            attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(DogmaAttributeId.ShieldBonus),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed)
        );
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The shield booster is still cycling.");
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        var health = PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now);
        if (health.Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot boost its shields.");
        }
        double shield = ShieldBoostRules.RestoredShield(health, booster);
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, booster.CapacitorNeed, now);
        var saved =
            world.Set<ShipHealthState>().Find(pilot.ActiveShipId)
            ?? world.Set<ShipHealthState>().Add(new() { ShipId = pilot.ActiveShipId }).Entity;
        saved.ShieldCharge = shield;
        saved.UpdatedAt = Math.Max(now, saved.UpdatedAt);
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(booster.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        world.SaveChanges();
        transaction.Commit();
        return new(
            booster,
            health with
            {
                Remaining = health.Remaining with { Shield = shield },
            },
            capacitor,
            shield - health.Remaining.Shield
        );
    }
}
