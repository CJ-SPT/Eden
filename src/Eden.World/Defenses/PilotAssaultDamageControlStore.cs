using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Defenses;

[Injectable]
internal sealed class PilotAssaultDamageControlStore(WorldDatabase database) : IPilotAssaultDamageControls
{
    public AssaultDamageControlActivation Activate(long pilotId, long moduleId, long now, DogmaEffect? expectedEffect = null)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot.ActiveShip.Position is null
            || item is not { Singleton: true, Online: true }
            || item.OwnerId != pilotId
            || item.LocationId != pilot.ActiveShipId
            || !InventoryDefinitions.IsLowSlot(item.FlagId)
            || !AssaultDamageControlCatalog.Supported(catalog, item.TypeId, catalog.FittingType(item.TypeId)?.GroupId ?? 0)
            || expectedEffect is not null && FittingCatalog.DefaultEffect(catalog, item.TypeId) != expectedEffect
            || !InventoryDefinitions.CanFitShip(
                catalog.FittingAttributes(item.TypeId),
                pilot.ActiveShip.TypeId,
                catalog.FittingType(pilot.ActiveShip.TypeId)!.GroupId
            )
        )
        {
            throw new InvalidDataException("Use an online fitted assault damage control on your eligible ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, item.Id, now);
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        var controls = calculation.Modules.Where(m => m.GroupId == AssaultDamageControlRules.Group).Select(m => m.Item.Id).ToArray();
        if (
            world
                .Set<ActiveFittedModule>()
                .Any(a =>
                    a.ShipId == pilot.ActiveShipId
                    && a.ModuleId != moduleId
                    && Enumerable.Contains(controls, a.ModuleId)
                    && (a.ExpiresAt == 0 || a.ExpiresAt > now)
                )
        )
        {
            throw new InvalidDataException("Only one assault damage control can be active on a ship.");
        }
        var attributes = calculation.Graph.Attributes(moduleId);
        var module =
            item.TypeId == BreacherRules.CounterType
                ? new AssaultDamageControl(
                    moduleId,
                    item.TypeId,
                    attributes[DogmaAttributeId.Duration],
                    attributes[DogmaAttributeId.CapacitorNeed],
                    0,
                    1
                )
                : AssaultDamageControlRules.Define(moduleId, item.TypeId, attributes);
        var schedule = AssaultDamageControlRules.Schedule(module, now);
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (
            cooldown?.ReadyAt > now
            || world.Set<ActiveFittedModule>().Any(a => a.ModuleId == moduleId && (a.ExpiresAt == 0 || a.ExpiresAt > now))
        )
        {
            throw new InvalidDataException("The assault damage control is still active or waiting for reactivation.");
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate an assault damage control.");
        }
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, module.CapacitorNeed, now);
        if (item.TypeId == BreacherRules.CounterType)
        {
            var condition = ModuleConditionPersistence.Read(world, catalog, item);
            if (condition.BurnedOut)
            {
                throw new InvalidDataException("Repair the burned-out Breach Control before activating it.");
            }
            var saved =
                world.Set<SavedModuleCondition>().Find(item.Id) ?? world.Add(new SavedModuleCondition { ModuleId = item.Id }).Entity;
            saved.Damage = attributes[DogmaAttributeId.StructureHitPoints];
        }
        var active =
            world.Set<ActiveFittedModule>().Find(moduleId)
            ?? world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = pilot.ActiveShipId }).Entity;
        active.ExpiresAt = schedule.ExpiresAt;
        active.ShipId = pilot.ActiveShipId;
        cooldown ??= world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = schedule.ReadyAt;
        world.SaveChanges();
        var updated = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        transaction.Commit();
        return new(module, schedule, capacitor, updated);
    }

    public bool Active(long pilotId, long shipId, long moduleId, long now) =>
        LiveReadModels.Find(database.Path)?.AssaultDamageControlActive(pilotId, shipId, moduleId, now, ReadActive)
        ?? ReadActive(pilotId, shipId, moduleId) > now;

    private sealed record ActiveMembership(int TypeId, long ExpiresAt);

    private long ReadActive(long pilotId, long shipId, long moduleId)
    {
        using WorldActionScope inspection = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = inspection.World;
        var active = world.Set<ActiveFittedModule>().Find(moduleId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        ActiveMembership? membership =
            active?.ShipId == shipId
            && item is not null
            && item.OwnerId == pilotId
            && item.LocationId == shipId
            && item.Online
            && item.Singleton
            && item.FlagId >= (int)InventoryFlag.LowSlot0
            && item.FlagId <= (int)InventoryFlag.LowSlot7
                ? new(item.TypeId, active.ExpiresAt)
                : null;
        using CatalogDbContext catalog = new(database.CatalogPath);
        return
            membership is not null
            && AssaultDamageControlCatalog.Supported(catalog, membership.TypeId, catalog.FittingType(membership.TypeId)?.GroupId ?? 0)
            ? membership.ExpiresAt
            : 0;
    }

    public IReadOnlyDictionary<DogmaAttributeId, double> Stop(long pilotId, long shipId, long moduleId, long expiresAt)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (!world.Ships.Any(s => s.Id == shipId && s.OwnerId == pilotId))
        {
            return new Dictionary<DogmaAttributeId, double>();
        }
        // An old session's cleanup must not remove a later paid activation.
        if (world.Set<ActiveFittedModule>().Find(moduleId) is { } active && active.ShipId == shipId && active.ExpiresAt == expiresAt)
        {
            world.Remove(active);
            if (world.Set<OwnedItem>().Find(moduleId) is { TypeId: BreacherRules.CounterType } item)
            {
                item.Online = false;
            }
        }
        world.SaveChanges();
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var result = PilotFittingStore.ShipAttributes(world, catalog, pilot, world.Ships.Find(shipId)!);
        transaction.Commit();
        return result;
    }
}
