using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Propulsion;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotPropulsionStore(WorldDatabase database) : IPilotPropulsion
{
    private static double Bonus(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        NavigationSkill skill,
        DogmaAttributeId source
    ) =>
        PilotSkillReader.Level(world, catalog, pilotId, (int)skill)
        * (catalog.Set<TypeAttribute>().Find((int)skill, (int)source)?.Value ?? 0);

    internal static double NavigationSpeed(WorldDbContext world, CatalogDbContext catalog, long pilotId, double speed) =>
        PropulsionRules.NavigationSpeed(speed, Bonus(world, catalog, pilotId, NavigationSkill.Navigation, DogmaAttributeId.VelocityBonus));

    public double MaxSpeed(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        int typeId = world.Pilots.Where(p => p.Id == pilotId).Select(p => p.ActiveShip.TypeId).Single();
        double speed = catalog.Set<TypeAttribute>().Find(typeId, (int)DogmaAttributeId.MaxVelocity)?.Value ?? 0;
        return NavigationSpeed(world, catalog, pilotId, speed);
    }

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
        }
            .Where(b => b.Percent != 0)
            .ToArray();
    }

    public IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        return ModuleAttributes(world, catalog, pilotId, module);
    }

    internal static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        InventoryItem module
    )
    {
        return PropulsionRules.ModuleAttributes(
            module.GroupId,
            PilotFittingStore.ReadAttributes(catalog, module.TypeId),
            NavigationBonuses(world, catalog, pilotId)
        );
    }

    public PropulsionCycle Activate(long pilotId, long moduleId, long now)
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
            || catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId != PropulsionDefinitions.AfterburnerGroup
        )
        {
            throw new InvalidDataException("Use an online fitted afterburner on your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        var item = PilotInventoryStore.Read(catalog, module);
        var attributes = ModuleAttributes(world, catalog, pilotId, item);
        Afterburner burner = new(
            moduleId,
            module.TypeId,
            attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            attributes.GetValueOrDefault(DogmaAttributeId.SpeedFactor),
            attributes.GetValueOrDefault(DogmaAttributeId.SpeedBoostFactor),
            attributes.GetValueOrDefault(DogmaAttributeId.MassAddition)
        );
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        double mass = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId).Mass ?? 0;
        _ = burner.Apply(new(0, 0, mass, 1, (float)ship.GetValueOrDefault(DogmaAttributeId.MaxVelocity), 1));
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The afterburner is still cycling.");
        }
        var groupTypes = catalog
            .Set<ItemType>()
            .Where(t => t.GroupId == PropulsionDefinitions.AfterburnerGroup)
            .Select(t => t.Id)
            .ToArray();
        var groupModules = world
            .Set<OwnedItem>()
            .Where(i => i.LocationId == pilot.ActiveShipId && groupTypes.Contains(i.TypeId))
            .Select(i => i.Id)
            .ToArray();
        int limit = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.MaxGroupActive, 1));
        if (limit <= 0 || world.Set<ModuleCooldown>().Count(c => groupModules.Contains(c.ModuleId) && c.ReadyAt > now) >= limit)
        {
            throw new InvalidDataException("Only one afterburner can run at a time.");
        }
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate its afterburner.");
        }
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, burner.CapacitorNeed, now);
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(burner.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        world.SaveChanges();
        transaction.Commit();
        return new(burner, capacitor);
    }
}
