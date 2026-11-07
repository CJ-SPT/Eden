using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
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
internal sealed class PilotHardenerStore(WorldDatabase database) : IPilotHardening
{
    public HardenerActivation Activate(long pilotId, long moduleId, DogmaEffect effect, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var module = world.Set<OwnedItem>().Find(moduleId);
        int group = module is null ? 0 : catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId;
        bool correctSlot =
            group == HardenerDefinitions.ShieldGroup
                ? module is not null && InventoryDefinitions.IsMediumSlot(module.FlagId)
                : module is not null && InventoryDefinitions.IsLowSlot(module.FlagId);
        if (
            pilot.ActiveShip.Position is null
            || module is null
            || module.OwnerId != pilotId
            || module.LocationId != pilot.ActiveShipId
            || !module.Singleton
            || !module.Online
            || !correctSlot
            || HardenerDefinitions.Effect(group) != effect
            || !HardenerCatalog.Supported(catalog, module.TypeId, group)
            || FittingCatalog.Definition(catalog, module.TypeId) is null
        )
        {
            throw new InvalidDataException("Use an online fitted hardener on your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        var attributes = PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, PilotInventoryStore.Read(catalog, module));
        var hardener = HardenerDefinitions.Define(moduleId, module.TypeId, group, attributes);
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The hardener is still cycling.");
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate a hardener.");
        }
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, hardener.CapacitorNeed, now);
        if (world.Set<ActiveFittedModule>().Find(moduleId) is null)
        {
            world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = pilot.ActiveShipId });
        }
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(hardener.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        world.SaveChanges();
        var updated = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        transaction.Commit();
        return new(hardener, capacitor, updated);
    }

    public IReadOnlyDictionary<DogmaAttributeId, double> Stop(long pilotId, long shipId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var ship = world.Ships.SingleOrDefault(s => s.Id == shipId && s.OwnerId == pilotId);
        if (ship is null)
        {
            return new Dictionary<DogmaAttributeId, double>();
        }
        var active = world.Set<ActiveFittedModule>().SingleOrDefault(a => a.ModuleId == moduleId && a.ShipId == shipId);
        if (active is not null)
        {
            world.Remove(active);
            world.SaveChanges();
        }
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot, ship);
        transaction.Commit();
        return attributes;
    }
}
