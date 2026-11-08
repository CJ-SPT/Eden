using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Propulsion;

[Injectable]
internal sealed class PilotPropulsionStore(WorldDatabase database, IPilotFitting fitting, TimeProvider clock) : IPilotPropulsion
{
    private static double Bonus(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        NavigationSkill skill,
        DogmaAttributeId source
    ) => PilotSkillReader.Level(world, catalog, pilotId, (int)skill) * (catalog.FittingAttributes((int)skill).GetValueOrDefault(source, 0));

    internal static double NavigationSpeed(WorldDbContext world, CatalogDbContext catalog, long pilotId, double speed) =>
        PropulsionRules.NavigationSpeed(speed, Bonus(world, catalog, pilotId, NavigationSkill.Navigation, DogmaAttributeId.VelocityBonus));

    public double MaxSpeed(long pilotId) => fitting.ShipAttributes(pilotId).GetValueOrDefault(DogmaAttributeId.MaxVelocity);

    public IReadOnlyList<NavigationBonus> NavigationBonuses(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return NavigationBonuses(world, catalog, pilotId);
    }

    internal static IReadOnlyList<NavigationBonus> NavigationBonuses(WorldDbContext world, CatalogDbContext catalog, long pilotId)
    {
        return new NavigationBonus[]
        {
            new(
                (int)NavigationSkill.Navigation,
                DogmaAttributeId.MaxVelocity,
                Bonus(world, catalog, pilotId, NavigationSkill.Navigation, DogmaAttributeId.VelocityBonus)
            ),
            new(
                (int)NavigationSkill.Afterburner,
                DogmaAttributeId.Duration,
                Bonus(world, catalog, pilotId, NavigationSkill.Afterburner, DogmaAttributeId.DurationBonus),
                (int)NavigationSkill.Afterburner
            ),
            new(
                (int)NavigationSkill.Afterburner,
                DogmaAttributeId.CapacitorNeed,
                Bonus(world, catalog, pilotId, NavigationSkill.Afterburner, DogmaAttributeId.CapacitorNeedBonus),
                (int)NavigationSkill.Afterburner
            ),
            new(
                (int)NavigationSkill.FuelConservation,
                DogmaAttributeId.CapacitorNeed,
                Bonus(world, catalog, pilotId, NavigationSkill.FuelConservation, DogmaAttributeId.CapacitorNeedBonus),
                (int)NavigationSkill.Afterburner
            ),
            new(
                (int)NavigationSkill.AccelerationControl,
                DogmaAttributeId.SpeedFactor,
                Bonus(world, catalog, pilotId, NavigationSkill.AccelerationControl, DogmaAttributeId.SpeedFactorBonus),
                Group: PropulsionDefinitions.AfterburnerGroup
            ),
            new(
                (int)NavigationSkill.HighSpeedManeuvering,
                DogmaAttributeId.CapacitorNeed,
                Bonus(world, catalog, pilotId, NavigationSkill.HighSpeedManeuvering, DogmaAttributeId.CapacitorNeedBonus),
                (int)NavigationSkill.HighSpeedManeuvering
            ),
        }
            .Where(b => b.Percent != 0)
            .ToArray();
    }

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
        InventoryItem module,
        FittingCalculation? calculation = null
    )
    {
        return PropulsionRules.ModuleAttributes(
            module.GroupId,
            PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, module, calculation),
            NavigationBonuses(world, catalog, pilotId)
        );
    }

    public PropulsionCycle Activate(long pilotId, long moduleId, long now, DogmaEffect? effect = null) =>
        ActivateCycle(pilotId, moduleId, now, effect, false);

    // Native overload activation remains gated on the damage/burnout owner.
    internal PropulsionCycle ActivateOverloaded(long pilotId, long moduleId, long now, DogmaEffect? effect = null) =>
        ActivateCycle(pilotId, moduleId, now, effect, true);

    private PropulsionCycle ActivateCycle(long pilotId, long moduleId, long now, DogmaEffect? effect, bool overloaded)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        overloaded |= PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var module = world.Set<OwnedItem>().Find(moduleId);
        IReadOnlySet<DogmaEffect> effects = module is null ? new HashSet<DogmaEffect>() : FittingCatalog.Effects(catalog, module.TypeId);
        var activation = effects.Contains(DogmaEffect.Microwarpdrive) ? DogmaEffect.Microwarpdrive : DogmaEffect.Afterburner;
        if (
            pilot.ActiveShip.Position is null
            || module is null
            || module.OwnerId != pilotId
            || module.LocationId != pilot.ActiveShipId
            || !module.Singleton
            || !module.Online
            || !InventoryDefinitions.IsMediumSlot(module.FlagId)
            || catalog.RequireItem(module.TypeId).GroupId != PropulsionDefinitions.AfterburnerGroup
            || !effects.Contains(activation)
            || effect is not null && activation != effect
            || FittingCatalog.Definition(catalog, module.TypeId) is null
        )
        {
            throw new InvalidDataException("Use an online fitted propulsion module on your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, module.Id, now);
        var calculation = overloaded
            ? FittingCalculation.Read(world, catalog, pilot, overloadedModules: new HashSet<long> { moduleId })
            : null;
        var item = PilotInventoryStore.Read(catalog, module);
        var attributes = ModuleAttributes(world, catalog, pilotId, item, calculation);
        if (attributes.GetValueOrDefault(DogmaAttributeId.ActivationBlocked) > 0)
        {
            throw new InvalidDataException("A warp scrambler is blocking the microwarpdrive.");
        }
        PropulsionModule burner = new(
            moduleId,
            module.TypeId,
            attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            attributes.GetValueOrDefault(DogmaAttributeId.SpeedFactor),
            attributes.GetValueOrDefault(DogmaAttributeId.SpeedBoostFactor),
            attributes.GetValueOrDefault(DogmaAttributeId.MassAddition),
            activation
        );
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        double mass = ship.GetValueOrDefault(DogmaAttributeId.Mass);
        _ = burner.Apply(new(0, 0, mass, 1, (float)ship.GetValueOrDefault(DogmaAttributeId.MaxVelocity), 1));
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The propulsion module is still cycling.");
        }
        var groupTypes = catalog.Definitions.TypesInGroup(PropulsionDefinitions.AfterburnerGroup).ToArray();
        var groupModules = world
            .Set<OwnedItem>()
            .Where(i => i.LocationId == pilot.ActiveShipId && groupTypes.Contains(i.TypeId))
            .Select(i => i.Id)
            .ToArray();
        int limit = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.MaxGroupActive, 1));
        if (limit <= 0 || world.Set<ModuleCooldown>().Count(c => groupModules.Contains(c.ModuleId) && c.ReadyAt > now) >= limit)
        {
            throw new InvalidDataException("Only one propulsion module can run at a time.");
        }
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate a propulsion module.");
        }
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, burner.CapacitorNeed, now);
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(burner.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        if (world.Set<ActiveFittedModule>().Find(moduleId) is null)
        {
            world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = pilot.ActiveShipId });
        }
        var heat = calculation is null
            ? null
            : ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, moduleId, now, cooldown.ReadyAt);
        world.SaveChanges();
        double signature = PilotFittingStore.ShipAttributes(world, catalog, pilotId).GetValueOrDefault(DogmaAttributeId.SignatureRadius);
        transaction.Commit();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, pilot.ActiveShipId, heat);
        }
        return new(burner, capacitor, signature);
    }

    public double SignatureRadius(long pilotId) => fitting.ShipAttributes(pilotId).GetValueOrDefault(DogmaAttributeId.SignatureRadius);

    public double Stop(long pilotId, long shipId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var ship = world.Ships.SingleOrDefault(s => s.Id == shipId && s.OwnerId == pilotId);
        if (ship is null)
        {
            return 0;
        }
        var active = world.Set<ActiveFittedModule>().SingleOrDefault(a => a.ModuleId == moduleId && a.ShipId == shipId);
        if (active is not null)
        {
            ShipHeatPersistence.CancelSource(world, pilotId, shipId, moduleId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            world.Remove(active);
            world.SaveChanges();
        }
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        double signature = PilotFittingStore
            .ShipAttributes(world, catalog, pilot, ship)
            .GetValueOrDefault(DogmaAttributeId.SignatureRadius);
        transaction.Commit();
        return signature;
    }
}
