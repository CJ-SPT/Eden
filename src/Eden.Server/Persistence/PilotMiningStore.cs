using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Protocol.Mining;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotMiningStore(WorldDatabase database, TimeProvider clock) : IPilotMining
{
    private sealed record ValidatedMining(Pilot Pilot, WorldAsteroid Asteroid, MiningLaser Laser);

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
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        var levels = Enum.GetValues<MiningSkill>().ToDictionary(s => s, s => PilotSkillReader.Level(world, catalog, pilotId, (int)s));
        return MiningRules.ModuleAttributes(
            new(
                module.GroupId,
                pilot.ActiveShip.TypeId,
                PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, module),
                PilotFittingStore.ReadAttributes(catalog, pilot.ActiveShip.TypeId),
                levels,
                HullCatalog.UsesCatalogBonuses(catalog, pilot.ActiveShip.TypeId)
            )
        );
    }

    private static ValidatedMining Validate(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        long moduleId,
        long asteroidId,
        long now
    )
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var module = PilotInventoryStore.List(world, catalog, pilotId, pilot.ActiveShipId).SingleOrDefault(i => i.Id == moduleId);
        var asteroid = world.Set<WorldAsteroid>().Find(asteroidId);
        if (
            pilot.ActiveShip.Position is not { } position
            || module is null
            || !module.Online
            || !module.Singleton
            || !InventoryDefinitions.IsHighSlot(module.FlagId)
            || module.GroupId != InventoryDefinitions.MiningLaserGroup
            || FittingCatalog.Definition(catalog, module.TypeId) is null
            || asteroid is null
            || asteroid.Quantity <= 0
        )
        {
            throw new InvalidDataException("Use an online mining laser on an available asteroid while in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        var ore = catalog.Set<ItemType>().Find(asteroid.TypeId);
        if (
            ore is null
            || catalog.Set<ItemGroup>().Find(ore.GroupId)?.CategoryId != AsteroidDefinitions.Category
            || !Enum.IsDefined((MiningResourceGroup)ore.GroupId)
        )
        {
            throw new InvalidDataException("Use an ore mining laser on ordinary ore, not ice, gas or Mercoxit.");
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot mine.");
        }
        int system = position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        var attributes = ModuleAttributes(world, catalog, pilotId, module);
        double range = attributes.GetValueOrDefault(DogmaAttributeId.MaxRange);
        double distance =
            new SpacePosition(position.X, position.Y, position.Z).DistanceTo(new(asteroid.X, asteroid.Y, asteroid.Z))
            - (hull.Radius ?? 0)
            - asteroid.Radius;
        if (asteroid.SystemId != system || distance > range)
        {
            throw new InvalidDataException("The asteroid is outside mining range.");
        }
        var laser = new MiningLaser(
            module.Id,
            module.TypeId,
            range,
            attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(DogmaAttributeId.MiningAmount),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalChance),
            attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalBonusYield),
            attributes.GetValueOrDefault(DogmaAttributeId.MiningResidueChance) / 100,
            attributes.GetValueOrDefault(DogmaAttributeId.MiningResidueMultiplier, 1)
        );
        if (
            !(laser.DurationMilliseconds > 0 && laser.Volume > 0)
            || !double.IsFinite(laser.DurationMilliseconds)
            || !double.IsFinite(laser.Volume)
        )
        {
            throw new InvalidDataException("Invalid mining cycle attributes.");
        }
        return new(pilot, asteroid, laser);
    }

    private static InventoryFlag OreHold(CatalogDbContext catalog, int hull) =>
        PilotInventoryStore.HoldCapacity(catalog, hull, (int)InventoryFlag.GeneralMiningHold) > 0
            ? InventoryFlag.GeneralMiningHold
            : InventoryFlag.Cargo;

    private static double Available(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        double capacity,
        InventoryFlag hold
    ) =>
        Math.Max(
            0,
            capacity
                - PilotInventoryStore
                    .List(world, catalog, pilotId, shipId)
                    .Where(i => i.FlagId == (int)hold)
                    .Sum(i => i.Volume * i.Quantity)
        );

    public MiningStart Begin(long pilotId, long moduleId, long asteroidId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var (pilot, asteroid, laser) = Validate(world, catalog, pilotId, moduleId, asteroidId, now);
        var hold = OreHold(catalog, pilot.ActiveShip.TypeId);
        double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilotId, pilot.ActiveShipId, (int)hold);
        double volume = PilotInventoryStore.Volume(catalog, asteroid.TypeId);
        double available = Available(world, catalog, pilotId, pilot.ActiveShipId, capacity, hold);
        if (available + InventoryDefinitions.ResourceTolerance < volume)
        {
            throw new CargoCapacityException(available, volume);
        }
        double charge = ShipResources.SpendCapacitor(
            world,
            pilot.ActiveShipId,
            PilotFittingStore.ShipAttributes(world, catalog, pilotId),
            laser.CapacitorNeed,
            now
        );
        world.SaveChanges();
        transaction.Commit();
        return new(laser, charge);
    }

    public MiningYield Collect(long pilotId, long moduleId, long asteroidId, MiningCollection collection)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var (pilot, asteroid, laser) = Validate(
            world,
            catalog,
            pilotId,
            moduleId,
            asteroidId,
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );
        var hold = OreHold(catalog, pilot.ActiveShip.TypeId);
        double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilotId, pilot.ActiveShipId, (int)hold);
        double oreVolume = PilotInventoryStore.Volume(catalog, asteroid.TypeId);
        if (!(oreVolume > 0))
        {
            throw new InvalidDataException("Invalid ore volume.");
        }
        var collected = MiningRules.Collect(
            asteroid.Quantity,
            Available(world, catalog, pilotId, pilot.ActiveShipId, capacity, hold),
            oreVolume,
            laser,
            collection
        );
        int extracted = collected.Extracted;
        int bonus = collected.CriticalBonus;
        InventoryChange? change = null;
        if (extracted + bonus > 0)
        {
            change = InventoryPersistence.MergeOutput(
                world,
                catalog,
                pilotId,
                pilot.ActiveShipId,
                (int)hold,
                asteroid.TypeId,
                checked(extracted + bonus)
            );
        }
        asteroid.Quantity -= (long)extracted + collected.Residue;
        world.SaveChanges();
        transaction.Commit();
        return new(change, asteroid.TypeId, extracted, bonus, collected.Residue, asteroid.Quantity == 0, collected.CargoFull);
    }
}
