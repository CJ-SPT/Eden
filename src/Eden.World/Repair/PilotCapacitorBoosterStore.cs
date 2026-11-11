using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
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
internal sealed class PilotCapacitorBoosterStore(WorldDatabase database) : IPilotCapacitorBoosting
{
    public CapacitorInjection Inject(long pilotId, long moduleId, long now) => Inject(pilotId, moduleId, now, false);

    internal CapacitorInjection InjectOverloaded(long pilotId, long moduleId, long now) => Inject(pilotId, moduleId, now, true);

    private CapacitorInjection Inject(long pilotId, long moduleId, long now, bool overloaded)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        overloaded |= PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var module = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot.ActiveShip.Position is null
            || module is null
            || module.OwnerId != pilotId
            || module.LocationId != pilot.ActiveShipId
            || !module.Singleton
            || !module.Online
            || !InventoryDefinitions.IsMediumSlot(module.FlagId)
            || catalog.RequireItem(module.TypeId).GroupId != CapacitorBoosterDefinitions.Group
            || FittingCatalog.Definition(catalog, module.TypeId) is null
        )
        {
            throw new InvalidDataException("Use an online fitted capacitor booster on your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, module.Id, now);
        var loaded = PilotAmmunitionStore.Contents(world, moduleId);
        if (
            loaded is null
            || loaded.OwnerId != pilotId
            || loaded.Quantity <= 0
            || catalog.RequireItem(loaded.TypeId).GroupId != CapacitorBoosterDefinitions.ChargeGroup
        )
        {
            throw new InvalidDataException("Load a compatible capacitor booster charge first.");
        }
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
        var booster = CapacitorBoosterDefinitions.Define(
            moduleId,
            module.TypeId,
            attributes,
            PilotFittingStore.ReadAttributes(catalog, loaded.TypeId)
        );
        if (loaded.Quantity < booster.ChargesPerCycle)
        {
            throw new InvalidDataException("Not enough loaded charges for this capacitor boost.");
        }
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The capacitor booster is still cycling or reloading.");
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot use its capacitor booster.");
        }
        double current = ShipResources.Capacitor(world, pilot.ActiveShipId, ship, now);
        double charge = CapacitorBoosterDefinitions.Inject(current, ship[DogmaAttributeId.CapacitorCapacity], booster.Amount);
        var energy =
            world.Set<ShipEnergy>().Find(pilot.ActiveShipId) ?? world.Set<ShipEnergy>().Add(new() { ShipId = pilot.ActiveShipId }).Entity;
        energy.Charge = charge;
        energy.UpdatedAt = Math.Max(now, energy.UpdatedAt);
        loaded.Quantity -= booster.ChargesPerCycle;
        var magazine = PilotAmmunitionStore.Read(world, module, loaded) with { ReadyAt = 0 };
        if (loaded.Quantity == 0)
        {
            world.Remove(loaded);
        }
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(booster.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        var heat = calculation is null
            ? null
            : ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, moduleId, now, cooldown.ReadyAt);
        world.SaveChanges();
        transaction.Commit();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, pilot.ActiveShipId, heat);
        }
        return new(booster, charge, magazine);
    }
}
