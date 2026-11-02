using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Protocol.Mining;
using Eden.Protocol.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotMiningStore(WorldDatabase database, IPilotInventory inventory, IPilotFitting fitting) : IPilotMining
{
    public IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module)
    {
        var attributes = fitting.Attributes(module.TypeId).ToDictionary();
        if (module.TypeId != InventoryDefinitions.CivilianMinerType) { return attributes; }
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        int Level(MiningSkill skill)
        {
            long points = world.Set<PilotSkill>().Find(pilotId, (int)skill)?.Points ?? 0;
            double rank = catalog.Set<TypeAttribute>().Where(a => a.ItemTypeId == (int)skill && a.AttributeId == (int)DogmaAttributeId.SkillTimeConstant)
                .Select(a => (double?)a.Value).SingleOrDefault() ?? 1;
            return new TrainedSkill((int)skill, points, rank).Level;
        }
        attributes[DogmaAttributeId.MiningAmount] = attributes.GetValueOrDefault(DogmaAttributeId.MiningAmount) *
            (1 + MiningDefinitions.YieldBonusPerSkillLevel * Level(MiningSkill.Mining)) *
            (1 + MiningDefinitions.YieldBonusPerSkillLevel * Level(MiningSkill.Astrogeology));
        attributes[DogmaAttributeId.MiningCriticalChance] = attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalChance) *
            (1 + MiningDefinitions.CriticalChanceBonusPerSkillLevel * Level(MiningSkill.Precision));
        attributes[DogmaAttributeId.MiningCriticalBonusYield] = attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalBonusYield) *
            (1 + MiningDefinitions.CriticalYieldBonusPerSkillLevel * Level(MiningSkill.Exploitation));
        return attributes;
    }

    public double Capacitor(long pilotId, long now)
    {
        using WorldDbContext world = new(database.Path);
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        return Charge(world.Set<ShipEnergy>().Find(pilot.ActiveShipId), fitting.ShipAttributes(pilotId), now);
    }

    private static double Charge(ShipEnergy? saved, IReadOnlyDictionary<DogmaAttributeId, double> attributes, long now)
    {
        double capacity = attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity);
        return saved is null ? capacity : MiningDefinitions.Recharged(saved.Charge, capacity,
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime),
            Math.Max(0, (now - saved.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond));
    }

    private (Pilot Pilot, WorldAsteroid Asteroid, MiningLaser Laser) Validate(WorldDbContext world, long pilotId, long moduleId, long asteroidId)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var module = inventory.List(pilotId, pilot.ActiveShipId).SingleOrDefault(i => i.Id == moduleId);
        var asteroid = world.Set<WorldAsteroid>().Find(asteroidId);
        if (pilot.ActiveShip.Position is not { } position || module is null || !module.Online || !module.Singleton ||
            !InventoryDefinitions.IsHighSlot(module.FlagId) || module.TypeId != InventoryDefinitions.CivilianMinerType ||
            asteroid is null || asteroid.Quantity <= 0)
        {
            throw new InvalidDataException("Use an online Civilian Miner on an available asteroid while in space.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        if (hull.GroupId != MiningDefinitions.CorvetteGroup)
        {
            throw new InvalidDataException("Mining activation currently supports corvettes.");
        }
        int system = position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        var attributes = ModuleAttributes(pilotId, module);
        double range = attributes.GetValueOrDefault(DogmaAttributeId.MaxRange);
        double distance = new SpacePosition(position.X, position.Y, position.Z).DistanceTo(new(asteroid.X, asteroid.Y, asteroid.Z)) -
            (hull.Radius ?? 0) - asteroid.Radius;
        if (asteroid.SystemId != system || distance > range)
        {
            throw new InvalidDataException("The asteroid is outside mining range.");
        }
        var laser = new MiningLaser(module.Id, module.TypeId, range, attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(DogmaAttributeId.MiningAmount), attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalChance), attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalBonusYield));
        if (!(laser.DurationMilliseconds > 0 && laser.Volume > 0) || !double.IsFinite(laser.DurationMilliseconds) || !double.IsFinite(laser.Volume))
        {
            throw new InvalidDataException("Invalid mining cycle attributes.");
        }
        return (pilot, asteroid, laser);
    }

    private double Available(long pilotId, long shipId, double capacity) => Math.Max(0, capacity -
        inventory.List(pilotId, shipId).Where(i => i.FlagId == (int)InventoryFlag.Cargo).Sum(i => i.Volume * i.Quantity));

    public MiningStart Begin(long pilotId, long moduleId, long asteroidId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var (pilot, asteroid, laser) = Validate(world, pilotId, moduleId, asteroidId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        double capacity = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId).Capacity ?? 0;
        double volume = PilotInventoryStore.Volume(catalog, asteroid.TypeId);
        double available = Available(pilotId, pilot.ActiveShipId, capacity);
        if (available + InventoryDefinitions.ResourceTolerance < volume)
        {
            throw new CargoCapacityException(available, volume);
        }
        var saved = world.Set<ShipEnergy>().Find(pilot.ActiveShipId);
        double charge = Charge(saved, fitting.ShipAttributes(pilotId), now);
        if (charge + InventoryDefinitions.ResourceTolerance < laser.CapacitorNeed)
        {
            throw new InvalidDataException("Insufficient capacitor to activate the mining laser.");
        }
        if (saved is null) { saved = world.Set<ShipEnergy>().Add(new() { ShipId = pilot.ActiveShipId }).Entity; }
        saved.Charge = Math.Max(0, charge - laser.CapacitorNeed);
        saved.UpdatedAt = now;
        world.SaveChanges();
        transaction.Commit();
        return new(laser, saved.Charge);
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
        double capacity = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId).Capacity ?? 0;
        double oreVolume = PilotInventoryStore.Volume(catalog, asteroid.TypeId);
        if (!(oreVolume > 0)) { throw new InvalidDataException("Invalid ore volume."); }
        int free = (int)Math.Min(int.MaxValue, Math.Floor((Available(pilotId, pilot.ActiveShipId, capacity) + InventoryDefinitions.ResourceTolerance) / oreVolume));
        int extracted = (int)Math.Min(Math.Min(asteroid.Quantity, free), Math.Floor((volume + InventoryDefinitions.ResourceTolerance) / oreVolume));
        // Critical successes add bonus ore without reducing the remaining deposit.
        int bonus = (int)Math.Min(free - extracted, Math.Floor(extracted * criticalBonusYield));
        InventoryChange? change = null;
        if (extracted + bonus > 0)
        {
            var stack = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == pilot.ActiveShipId &&
                i.FlagId == (int)InventoryFlag.Cargo && i.TypeId == asteroid.TypeId && !i.Singleton).OrderBy(i => i.Id).FirstOrDefault();
            var previous = stack is null ? null : PilotInventoryStore.Read(catalog, stack);
            stack ??= world.Set<OwnedItem>().Add(new()
            {
                Id = PilotInventoryStore.NextId(world),
                OwnerId = pilotId,
                LocationId = pilot.ActiveShipId,
                TypeId = asteroid.TypeId,
                FlagId = (int)InventoryFlag.Cargo,
            }).Entity;
            stack.Quantity = checked(stack.Quantity + extracted + bonus);
            asteroid.Quantity -= extracted;
            change = new(PilotInventoryStore.Read(catalog, stack), previous);
        }
        world.SaveChanges();
        transaction.Commit();
        return new(change, asteroid.TypeId, extracted, bonus, asteroid.Quantity == 0, free <= extracted + bonus);
    }
}
