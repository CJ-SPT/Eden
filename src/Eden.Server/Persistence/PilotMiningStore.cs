using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Protocol.Mining;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotMiningStore(WorldDatabase database, IPilotInventory inventory, IPilotFitting fitting) : IPilotMining
{
    public IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module)
    {
        var attributes = fitting.Attributes(module.TypeId).ToDictionary();
        if (module.GroupId != InventoryDefinitions.MiningLaserGroup)
        {
            return attributes;
        }
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        int Level(MiningSkill skill)
        {
            return PilotSkillReader.Level(world, catalog, pilotId, (int)skill);
        }
        attributes[DogmaAttributeId.MiningAmount] =
            attributes.GetValueOrDefault(DogmaAttributeId.MiningAmount)
            * (1 + MiningDefinitions.YieldBonusPerSkillLevel * Level(MiningSkill.Mining))
            * (1 + MiningDefinitions.YieldBonusPerSkillLevel * Level(MiningSkill.Astrogeology));
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        if (pilot.ActiveShip.TypeId == (int)StarterShipType.Venture)
        {
            // Build 3662168 effects 5058/5139 multiply ore yield by hull attribute
            // 207, then apply attribute 1842 percent for each Mining Frigate level.
            var hull = fitting.Attributes(pilot.ActiveShip.TypeId);
            attributes[DogmaAttributeId.MiningAmount] *=
                hull.GetValueOrDefault(DogmaAttributeId.MiningRoleYieldMultiplier, 1)
                * (1 + hull.GetValueOrDefault(DogmaAttributeId.MiningFrigateYieldBonus) * Level(MiningSkill.Frigate) / 100);
        }
        attributes[DogmaAttributeId.MiningCriticalChance] =
            attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalChance)
            * (1 + MiningDefinitions.CriticalChanceBonusPerSkillLevel * Level(MiningSkill.Precision));
        attributes[DogmaAttributeId.MiningCriticalBonusYield] =
            attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalBonusYield)
            * (1 + MiningDefinitions.CriticalYieldBonusPerSkillLevel * Level(MiningSkill.Exploitation));
        return attributes;
    }

    public double Capacitor(long pilotId, long now)
    {
        using WorldDbContext world = new(database.Path);
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        return ShipResources.Capacitor(world, pilot.ActiveShipId, fitting.ShipAttributes(pilotId), now);
    }

    private (Pilot Pilot, WorldAsteroid Asteroid, MiningLaser Laser) Validate(
        WorldDbContext world,
        long pilotId,
        long moduleId,
        long asteroidId
    )
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var module = inventory.List(pilotId, pilot.ActiveShipId).SingleOrDefault(i => i.Id == moduleId);
        var asteroid = world.Set<WorldAsteroid>().Find(asteroidId);
        if (
            pilot.ActiveShip.Position is not { } position
            || module is null
            || !module.Online
            || !module.Singleton
            || !InventoryDefinitions.IsHighSlot(module.FlagId)
            || module.GroupId != InventoryDefinitions.MiningLaserGroup
            || asteroid is null
            || asteroid.Quantity <= 0
        )
        {
            throw new InvalidDataException("Use an online mining laser on an available asteroid while in space.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        if (hull.GroupId != MiningDefinitions.CorvetteGroup && hull.Id != (int)StarterShipType.Venture)
        {
            throw new InvalidDataException("Mining activation currently supports corvettes and the Venture.");
        }
        int system = position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        var attributes = ModuleAttributes(pilotId, module);
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
            attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalBonusYield)
        );
        if (
            !(laser.DurationMilliseconds > 0 && laser.Volume > 0)
            || !double.IsFinite(laser.DurationMilliseconds)
            || !double.IsFinite(laser.Volume)
        )
        {
            throw new InvalidDataException("Invalid mining cycle attributes.");
        }
        return (pilot, asteroid, laser);
    }

    private static InventoryFlag OreHold(CatalogDbContext catalog, int hull) =>
        PilotInventoryStore.HoldCapacity(catalog, hull, (int)InventoryFlag.GeneralMiningHold) > 0
            ? InventoryFlag.GeneralMiningHold
            : InventoryFlag.Cargo;

    private double Available(long pilotId, long shipId, double capacity, InventoryFlag hold) =>
        Math.Max(0, capacity - inventory.List(pilotId, shipId).Where(i => i.FlagId == (int)hold).Sum(i => i.Volume * i.Quantity));

    public MiningStart Begin(long pilotId, long moduleId, long asteroidId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var (pilot, asteroid, laser) = Validate(world, pilotId, moduleId, asteroidId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var hold = OreHold(catalog, pilot.ActiveShip.TypeId);
        double capacity = PilotInventoryStore.HoldCapacity(catalog, pilot.ActiveShip.TypeId, (int)hold);
        double volume = PilotInventoryStore.Volume(catalog, asteroid.TypeId);
        double available = Available(pilotId, pilot.ActiveShipId, capacity, hold);
        if (available + InventoryDefinitions.ResourceTolerance < volume)
        {
            throw new CargoCapacityException(available, volume);
        }
        double charge = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, fitting.ShipAttributes(pilotId), laser.CapacitorNeed, now);
        world.SaveChanges();
        transaction.Commit();
        return new(laser, charge);
    }

    public MiningYield Collect(long pilotId, long moduleId, long asteroidId, double volume, double criticalBonusYield)
    {
        if (!(volume >= 0) || !double.IsFinite(volume) || !(criticalBonusYield >= 0) || !double.IsFinite(criticalBonusYield))
        {
            throw new InvalidDataException("Invalid mining yield.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var (pilot, asteroid, _) = Validate(world, pilotId, moduleId, asteroidId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var hold = OreHold(catalog, pilot.ActiveShip.TypeId);
        double capacity = PilotInventoryStore.HoldCapacity(catalog, pilot.ActiveShip.TypeId, (int)hold);
        double oreVolume = PilotInventoryStore.Volume(catalog, asteroid.TypeId);
        if (!(oreVolume > 0))
        {
            throw new InvalidDataException("Invalid ore volume.");
        }
        int free = (int)
            Math.Min(
                int.MaxValue,
                Math.Floor((Available(pilotId, pilot.ActiveShipId, capacity, hold) + InventoryDefinitions.ResourceTolerance) / oreVolume)
            );
        int extracted = (int)
            Math.Min(Math.Min(asteroid.Quantity, free), Math.Floor((volume + InventoryDefinitions.ResourceTolerance) / oreVolume));
        // Critical successes add bonus ore without reducing the remaining deposit.
        int bonus = (int)Math.Min(free - extracted, Math.Floor(extracted * criticalBonusYield));
        InventoryChange? change = null;
        if (extracted + bonus > 0)
        {
            var stack = world
                .Set<OwnedItem>()
                .Where(i =>
                    i.OwnerId == pilotId
                    && i.LocationId == pilot.ActiveShipId
                    && i.FlagId == (int)hold
                    && i.TypeId == asteroid.TypeId
                    && !i.Singleton
                )
                .OrderBy(i => i.Id)
                .FirstOrDefault();
            var previous = stack is null ? null : PilotInventoryStore.Read(catalog, stack);
            stack ??= world
                .Set<OwnedItem>()
                .Add(
                    new()
                    {
                        Id = PilotInventoryStore.NextId(world),
                        OwnerId = pilotId,
                        LocationId = pilot.ActiveShipId,
                        TypeId = asteroid.TypeId,
                        FlagId = (int)hold,
                    }
                )
                .Entity;
            stack.Quantity = checked(stack.Quantity + extracted + bonus);
            asteroid.Quantity -= extracted;
            change = new(PilotInventoryStore.Read(catalog, stack), previous);
        }
        world.SaveChanges();
        transaction.Commit();
        return new(change, asteroid.TypeId, extracted, bonus, asteroid.Quantity == 0, free <= extracted + bonus);
    }
}
