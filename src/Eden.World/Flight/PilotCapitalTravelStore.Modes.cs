using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Propulsion;
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
            throw new InvalidDataException("Use an online mode module on a compatible ship in space.");
        }
        if (action.Set<ModuleCooldown>().Find(moduleId)?.ReadyAt > now)
        {
            throw new InvalidDataException("The mode module is still cycling.");
        }
        PilotSkillReader.CheckRequirements(action, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(action, catalog, moduleId, now);
        RequireLiving(action, pilot, PilotFittingStore.ShipAttributes(action, catalog, pilot), now);
        long end = checked(now + CapitalModeRules.DurationMilliseconds(effect) * TimeSpan.TicksPerMillisecond);
        IReadOnlyList<InventoryChange> changes = [];
        if (effect == DogmaEffect.Bastion)
        {
            BeginBastion(action, catalog, pilot, shipId, now, end);
        }
        else
        {
            changes = SpendModeFuel(action, catalog, pilot, shipId, item.TypeId, effect);
        }
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

    private static void BeginBastion(WorldAction world, CatalogDbContext catalog, Pilot pilot, long ship, long now, long end)
    {
        foreach (var fitted in world.FittedItems(pilot.Id, ship))
        {
            if (
                InventoryDefinitions.IsMediumSlot(fitted.FlagId)
                && catalog.FittingType(fitted.TypeId)?.GroupId == PropulsionDefinitions.AfterburnerGroup
            )
            {
                PilotPropulsionStore.StopActiveCycle(world, pilot.Id, ship, fitted.Id, now);
            }
        }
        if (world.Set<SavedPilotTether>().Find(ship) is { } tether)
        {
            world.Remove(tether);
        }
        var crimewatch = CrimewatchPersistence.State(world, pilot.Id);
        crimewatch.SystemId = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        var timers = crimewatch.Timers();
        crimewatch.Apply(timers with { Weapons = CrimewatchRules.Extend(timers.Weapons, end, CrimewatchRules.WeaponsDuration) });
    }

    private static List<InventoryChange> SpendModeFuel(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        long shipId,
        int moduleType,
        DogmaEffect effect
    )
    {
        var attributes = catalog.FittingAttributes(moduleType);
        int skill = CapitalModeRules.RequiredSkill(effect);
        double bonus = catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.ConsumptionQuantityBonus);
        double required =
            attributes[DogmaAttributeId.ConsumptionQuantity] - PilotSkillReader.Level(world, catalog, pilot.Id, skill) * bonus;
        int fuelType = checked((int)attributes[DogmaAttributeId.ConsumptionType]);
        bool industrial = effect == DogmaEffect.CapitalIndustrialCore;
        if (industrial)
        {
            double role = catalog.FittingAttributes(pilot.ActiveShip.TypeId).GetValueOrDefault(DogmaAttributeId.IndustrialCoreFuelPercent);
            int level = PilotSkillReader.Level(world, catalog, pilot.Id, CapitalModeRules.CapitalIndustrialShips);
            required *= 1 + role * level / 100;
        }
        int quantity = checked((int)Math.Round(required, MidpointRounding.AwayFromZero));
        var cargo = FuelStacks(world, pilot.Id, shipId, InventoryFlag.Cargo, fuelType);
        var stacks = (industrial ? FuelStacks(world, pilot.Id, shipId, InventoryFlag.FuelBay, fuelType).Concat(cargo) : cargo).ToArray();
        RequireFuel(stacks, quantity);
        return SpendFuel(world, catalog, stacks, quantity);
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
                long start = active.ExpiresAt - CapitalModeRules.DurationMilliseconds(effect) * TimeSpan.TicksPerMillisecond;
                return new(item.OwnerId, shipId, item.Id, item.TypeId, effect, start, active.ExpiresAt);
            }
        }
        return null;
    }
}
