using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Thermal;

namespace Eden.World.Flight;

internal sealed partial class PilotCapitalTravelStore
{
    public CapitalModeActivation ActivateMode(long pilotId, long shipId, long moduleId, DogmaEffect effect)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        var pilot = ActivePilot(action, pilotId, shipId);
        RequireReady(action, pilot, now);
        var item = action.Set<OwnedItem>().Find(moduleId);
        int hull = catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId;
        if (
            item is not { Online: true, Singleton: true }
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || CapitalModeRules.Effect(item.TypeId) != effect
            || !CapitalModeCatalog.Supported(catalog, item.TypeId)
            || !InventoryDefinitions.CanFitShip(catalog.FittingAttributes(item.TypeId), pilot.ActiveShip.TypeId, hull)
            || !CapitalModeRules.CanActivate(effect, hull)
        )
        {
            throw new InvalidDataException("Use an online capital mode module on a compatible ship in space.");
        }
        if (action.Set<ModuleCooldown>().Find(moduleId)?.ReadyAt > now)
        {
            throw new InvalidDataException("The capital mode is still cycling.");
        }
        PilotSkillReader.CheckRequirements(action, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(action, catalog, moduleId, now);
        RequireLiving(action, pilot, PilotFittingStore.ShipAttributes(action, catalog, pilot), now);
        var attributes = catalog.FittingAttributes(item.TypeId);
        int skill = CapitalModeRules.RequiredSkill(effect);
        double bonus = catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.ConsumptionQuantityBonus);
        double required =
            attributes[DogmaAttributeId.ConsumptionQuantity] - PilotSkillReader.Level(action, catalog, pilotId, skill) * bonus;
        int fuelType = checked((int)attributes[DogmaAttributeId.ConsumptionType]);
        bool industrial = effect == DogmaEffect.CapitalIndustrialCore;
        if (industrial)
        {
            double role = catalog.FittingAttributes(pilot.ActiveShip.TypeId).GetValueOrDefault(DogmaAttributeId.IndustrialCoreFuelPercent);
            int level = PilotSkillReader.Level(action, catalog, pilotId, CapitalModeRules.CapitalIndustrialShips);
            required *= 1 + role * level / 100;
        }
        int quantity = checked((int)Math.Round(required, MidpointRounding.AwayFromZero));
        var cargo = FuelStacks(action, pilotId, shipId, InventoryFlag.Cargo, fuelType);
        var stacks = (industrial ? FuelStacks(action, pilotId, shipId, InventoryFlag.FuelBay, fuelType).Concat(cargo) : cargo).ToArray();
        RequireFuel(stacks, quantity);
        var changes = SpendFuel(action, catalog, stacks, quantity);

        long end = checked(now + CapitalModeRules.CycleMilliseconds * TimeSpan.TicksPerMillisecond);
        var active =
            action.Set<ActiveFittedModule>().Find(moduleId)
            ?? action.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = shipId }).Entity;
        active.ShipId = shipId;
        active.ExpiresAt = end;
        var cooldown = action.Set<ModuleCooldown>().Find(moduleId) ?? action.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = end;
        action.SaveChanges();
        var updated = PilotFittingStore.ShipAttributes(action, catalog, pilotId);
        action.Commit();
        return new(new(pilotId, shipId, moduleId, item.TypeId, effect, now, end), changes, updated);
    }

    public CapitalModeCycle? Mode(long shipId) => LiveWorld.Find(database.Path)?.Economy.Inspect(world => ActiveMode(world, shipId, Now));

    internal static CapitalModeCycle? ActiveMode(WorldAction world, long shipId, long now)
    {
        if (world is not EconomyTransaction transaction)
        {
            return null;
        }
        foreach (long module in transaction.TimedModules(shipId))
        {
            if (
                world.Set<ActiveFittedModule>().Find(module) is { } active
                && active.ShipId == shipId
                && active.ExpiresAt > now
                && world.Set<OwnedItem>().Find(module) is { Online: true, Singleton: true } item
                && item.LocationId == shipId
                && InventoryDefinitions.IsHighSlot(item.FlagId)
                && CapitalModeRules.Effect(item.TypeId) is { } effect
            )
            {
                long start = active.ExpiresAt - CapitalModeRules.CycleMilliseconds * TimeSpan.TicksPerMillisecond;
                return new(item.OwnerId, shipId, item.Id, item.TypeId, effect, start, active.ExpiresAt);
            }
        }
        return null;
    }
}
