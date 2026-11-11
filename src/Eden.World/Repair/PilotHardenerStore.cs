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
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

[Injectable]
internal sealed class PilotHardenerStore(WorldDatabase database, TimeProvider clock) : IPilotHardening
{
    public HardenerActivation Activate(long pilotId, long moduleId, DogmaEffect effect, long now) =>
        Activate(pilotId, moduleId, effect, now, false);

    internal HardenerActivation ActivateOverloaded(long pilotId, long moduleId, DogmaEffect effect, long now) =>
        Activate(pilotId, moduleId, effect, now, true);

    private HardenerActivation Activate(long pilotId, long moduleId, DogmaEffect effect, long now, bool overloaded)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        overloaded |= PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var module = world.Set<OwnedItem>().Find(moduleId);
        int group = module is null ? 0 : catalog.RequireItem(module.TypeId).GroupId;
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
        NaniteRepairPersistence.RequireAvailable(world, catalog, module.Id, now);
        var calculation = overloaded
            ? FittingCalculation.Read(world, catalog, pilot, overloadedModules: new HashSet<long> { moduleId })
            : null;
        var attributes = PilotFittingStore.ReadModuleAttributes(
            world,
            catalog,
            pilotId,
            PilotInventoryStore.Read(catalog, module),
            calculation
        );
        var hardener = HardenerDefinitions.Define(moduleId, module.TypeId, group, attributes);
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The hardener is still cycling.");
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate a hardener.");
        }
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, hardener.CapacitorNeed, now);
        var active =
            world.Set<ActiveFittedModule>().Find(moduleId)
            ?? world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = pilot.ActiveShipId }).Entity;
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(hardener.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        active.ExpiresAt = overloaded ? cooldown.ReadyAt : 0;
        var heat = calculation is null
            ? null
            : ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, moduleId, now, cooldown.ReadyAt);
        world.SaveChanges();
        var updated = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        transaction.Commit();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, pilot.ActiveShipId, heat);
        }
        return new(hardener, capacitor, updated);
    }

    public IReadOnlyDictionary<DogmaAttributeId, double> Stop(long pilotId, long shipId, long moduleId)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var ship = world.Ships.SingleOrDefault(s => s.Id == shipId && s.OwnerId == pilotId);
        if (ship is null)
        {
            return new Dictionary<DogmaAttributeId, double>();
        }
        var active = world.Set<ActiveFittedModule>().SingleOrDefault(a => a.ModuleId == moduleId && a.ShipId == shipId);
        if (active is not null)
        {
            if (active.ExpiresAt > 0)
            {
                ShipHeatPersistence.CancelSource(world, pilotId, shipId, moduleId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            }
            world.Remove(active);
            world.SaveChanges();
        }
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot, ship);
        transaction.Commit();
        return attributes;
    }
}
