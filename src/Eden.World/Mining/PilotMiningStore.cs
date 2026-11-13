using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Agents;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

[Injectable]
internal sealed partial class PilotMiningStore(WorldDatabase database, TimeProvider clock) : IPilotMining
{
    private sealed record ValidatedMining(
        Pilot Pilot,
        WorldAsteroid Asteroid,
        MiningLaser Laser,
        IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes
    );

    public IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (LiveWorld.Find(database.Path) is { SqlConnections: 0 } live)
        {
            return live.Economy.Read(world => ModuleAttributes(world, catalog, pilotId, module), new(pilots: [pilotId]));
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var result = ModuleAttributes(world, catalog, pilotId, module);
        transaction.Commit();
        return result;
    }

    internal static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        InventoryItem module
    )
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        var skills = Enum.GetValues<MiningSkill>();
        var trained = PilotSkillReader.Levels(world, catalog, pilotId, skills.Select(skill => (int)skill).ToArray());
        var levels = skills.ToDictionary(skill => skill, skill => trained[(int)skill]);
        return MiningRules.ModuleAttributes(
            new(
                module.GroupId,
                pilot.ActiveShip.TypeId,
                PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, module),
                PilotFittingStore.ReadAttributes(catalog, pilot.ActiveShip.TypeId),
                levels,
                HullCatalog.UsesCatalogBonuses(catalog, pilot.ActiveShip.TypeId),
                WorldTuningRules.Yield(
                    world.Settings.Resources,
                    MiningDefinitions.Family(module.GroupId, catalog.FittingAttributes(module.TypeId))
                )
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
            || !InventoryDefinitions.IsMiningGroup(module.GroupId)
            || FittingCatalog.Definition(catalog, module.TypeId) is null
            || asteroid is null
            || asteroid.Quantity <= 0
        )
        {
            throw new InvalidDataException("Use an online mining laser on an available asteroid while in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        if (PilotAmmunitionStore.Contents(world, moduleId) is { } crystal)
        {
            var crystalType = catalog.RequireItem(crystal.TypeId);
            var crystalAttributes = catalog.FittingAttributes(crystal.TypeId);
            var targetTypes = CatalogTypeLists.Read(
                catalog,
                checked((long)crystalAttributes.GetValueOrDefault(DogmaAttributeId.MiningCrystalTypeList))
            );
            var target = catalog.RequireItem(asteroid.TypeId);
            if (
                crystal.OwnerId != pilotId
                || crystal.Quantity != 1
                || !MiningCrystalCatalog.Supported(catalog, crystal.TypeId, crystalType.GroupId)
                || !targetTypes.Contains(target.Id, target.GroupId, target.CategoryId)
            )
            {
                throw new InvalidDataException("The fitted mining crystal does not match this resource.");
            }
            PilotSkillReader.CheckRequirements(world, catalog, pilotId, crystal.TypeId);
        }
        NaniteRepairPersistence.RequireAvailable(world, catalog, module.Id, now);
        PilotFittingStore.CheckHull(catalog, module.TypeId, pilot.ActiveShip.TypeId);
        var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
        var ore = catalog.FittingType(asteroid.TypeId);
        var moduleAttributes = catalog.FittingAttributes(module.TypeId);
        var family = MiningDefinitions.Family(module.GroupId, moduleAttributes);
        bool missionDeposit = MissionMiningRules.IsDeposit(asteroid.Id);
        if (missionDeposit)
        {
            PilotMissionStore.RequireMiningAccess(world, asteroid.Id, pilotId, now);
        }
        bool compatible =
            family == MiningFamily.Gas
                ? ore?.CategoryId == MiningDefinitions.GasCloudCategory
                    && ore.GroupId == MiningDefinitions.GasCloudGroup
                    && (missionDeposit || MiningSiteCatalog.SupportsGas(ore.Id))
                : ore?.CategoryId == AsteroidDefinitions.Category
                    && (
                        family == MiningFamily.Ice
                            ? ore.GroupId == MiningDefinitions.IceGroup
                                && PilotInventoryStore.Volume(catalog, ore.Id) == MiningDefinitions.IceBlockVolume
                            : MiningDefinitions.MatchesOre(ore.GroupId, moduleAttributes)
                    );
        if (!compatible)
        {
            throw new InvalidDataException("The mining module does not match this resource.");
        }
        if (
            Eden.Gameplay.Structures.MoonMiningRules.IsMoonRock(asteroid.Id)
            && (
                world
                    .Set<Eden.Data.World.Structures.SavedMoonExtraction>()
                    .Find(Eden.Gameplay.Structures.MoonMiningRules.ExtractionId(asteroid.Id))
                    ?.FieldExpiresAt
                ?? 0
            ) <= now
        )
        {
            throw new InvalidDataException("The moon field is no longer available.");
        }
        if (MiningSiteRules.IsDeposit(asteroid.Id) && world.Set<SavedMiningDeposit>().Find(asteroid.Id) is { } deposit)
        {
            var site = world.Set<SavedMiningSite>().Find(deposit.SiteId);
            if (site is null || site.ReplaceAt is not null || site.ExpiresAt <= now)
            {
                throw new InvalidDataException("The resource site is no longer available.");
            }
        }
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot mine.");
        }
        int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
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
            missionDeposit || family == MiningFamily.Gas ? 0 : attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalChance),
            family == MiningFamily.Gas ? 0 : attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalBonusYield),
            missionDeposit ? 0 : attributes.GetValueOrDefault(DogmaAttributeId.MiningResidueChance) / 100,
            attributes.GetValueOrDefault(DogmaAttributeId.MiningResidueMultiplier, 1),
            family
        );
        if (
            !(laser.DurationMilliseconds > 0 && laser.Volume > 0)
            || !double.IsFinite(laser.DurationMilliseconds)
            || !double.IsFinite(laser.Volume)
        )
        {
            throw new InvalidDataException("Invalid mining cycle attributes.");
        }
        return new(pilot, asteroid, laser, ship);
    }

    private static InventoryFlag OreHold(CatalogDbContext catalog, int hull, MiningFamily family) =>
        family == MiningFamily.Gas && PilotInventoryStore.HoldCapacity(catalog, hull, (int)InventoryFlag.GasHold) > 0
            ? InventoryFlag.GasHold
        : family == MiningFamily.Ice && PilotInventoryStore.HoldCapacity(catalog, hull, (int)InventoryFlag.IceHold) > 0
            ? InventoryFlag.IceHold
        : PilotInventoryStore.HoldCapacity(catalog, hull, (int)InventoryFlag.GeneralMiningHold) > 0 ? InventoryFlag.GeneralMiningHold
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

    public MiningStart Begin(long pilotId, long moduleId, long asteroidId, long now, DogmaEffect? effect = null)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var start = Begin(transaction.World, catalog, pilotId, moduleId, asteroidId, now, effect);
        transaction.Commit();
        return start;
    }

    internal static MiningStart Begin(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        long moduleId,
        long asteroidId,
        long now,
        DogmaEffect? effect = null
    )
    {
        return BeginValidated(world, catalog, Validate(world, catalog, pilotId, moduleId, asteroidId, now), now, effect);
    }

    private static MiningStart BeginValidated(
        WorldDbContext world,
        CatalogDbContext catalog,
        ValidatedMining validated,
        long now,
        DogmaEffect? effect = null
    )
    {
        var (pilot, asteroid, laser, attributes) = validated;
        long pilotId = pilot.Id;
        long moduleId = laser.ItemId;
        if (effect is not null && effect != laser.Effect)
        {
            throw new InvalidDataException("The mining effect does not match the fitted module.");
        }
        if (world.Set<ModuleCooldown>().Find(moduleId) is { } existing && existing.ReadyAt > now)
        {
            throw new InvalidDataException("Wait for the mining cycle or crystal reload to finish.");
        }
        if (laser.Family == MiningFamily.Gas)
        {
            int limit = PilotSkillReader.Level(world, catalog, pilotId, (int)MiningSkill.GasHarvesting);
            var active = world
                .Set<ModuleCooldown>()
                .Where(c => c.Kind == ModuleCooldownKind.Cycle && c.ReadyAt > now)
                .Select(c => c.ModuleId)
                .ToArray();
            int count = world
                .Set<OwnedItem>()
                .Where(i => i.OwnerId == pilotId && i.LocationId == pilot.ActiveShipId && active.Contains(i.Id))
                .AsEnumerable()
                .Count(i =>
                    catalog.FittingType(i.TypeId)?.GroupId is InventoryDefinitions.GasScoopGroup or InventoryDefinitions.GasHarvesterGroup
                );
            if (count >= limit)
            {
                throw new InvalidDataException("Gas Cloud Harvesting limits your simultaneously active harvesters.");
            }
        }
        var hold = OreHold(catalog, pilot.ActiveShip.TypeId, laser.Family);
        double capacity = ShipHoldRules.Capacity((int)hold, attributes);
        double volume = PilotInventoryStore.Volume(catalog, asteroid.TypeId);
        double available = Available(world, catalog, pilotId, pilot.ActiveShipId, capacity, hold);
        if (available + InventoryDefinitions.ResourceTolerance < volume)
        {
            throw new CargoCapacityException(available, volume);
        }
        double charge = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, attributes, laser.CapacitorNeed, now);
        // Keep the already funded beam visible to authoritative repair checks,
        // even when its owning protocol controller has not collected it yet.
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId) ?? world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(laser.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        PilotTaskTrackingStore.Record(world, catalog, pilotId, now, TaskEvents.MiningLaserActivated);
        world.SaveChanges();
        return new(laser, charge);
    }

    public MiningYield Collect(long pilotId, long moduleId, long asteroidId, MiningCollection collection)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = Collect(
            transaction.World,
            catalog,
            pilotId,
            moduleId,
            asteroidId,
            collection,
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );
        transaction.Commit();
        return result;
    }

    internal static MiningYield Collect(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        long moduleId,
        long asteroidId,
        MiningCollection collection,
        long now
    ) => CollectPaid(world, catalog, pilotId, moduleId, asteroidId, collection, now, 1);

    private static MiningYield CollectPaid(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        long moduleId,
        long asteroidId,
        MiningCollection collection,
        long now,
        int cycles,
        ValidatedMining? validated = null
    )
    {
        var (pilot, asteroid, laser, attributes) = validated ?? Validate(world, catalog, pilotId, moduleId, asteroidId, now);
        if (cycles < 1 || cycles > 1 && PilotAmmunitionStore.Contents(world, moduleId) is not null)
        {
            throw new InvalidDataException("Crystal cycles must settle individually.");
        }
        laser = laser with { Volume = laser.Volume * cycles };
        if (world.Set<ModuleCooldown>().Find(moduleId) is not { Kind: ModuleCooldownKind.Cycle })
        {
            throw new InvalidDataException("There is no paid mining cycle to collect.");
        }
        var hold = OreHold(catalog, pilot.ActiveShip.TypeId, laser.Family);
        double capacity = ShipHoldRules.Capacity((int)hold, attributes);
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
        Magazine? crystalState = null;
        if (PilotAmmunitionStore.Contents(world, moduleId) is { } crystal)
        {
            var crystalAttributes = catalog.FittingAttributes(crystal.TypeId);
            double volatility = attributes.GetValueOrDefault(DogmaAttributeId.CrystalVolatilityChance, 1);
            var wear = CrystalRules.Use(crystalAttributes, crystal.CrystalDamage, collection.Rolls.Crystal, volatility);
            crystal.CrystalDamage = wear.Damage;
            crystal.Quantity = wear.Destroyed ? 0 : 1;
            var module = world.Set<OwnedItem>().Find(moduleId)!;
            crystalState = new(moduleId, pilot.ActiveShipId, module.FlagId, crystal.TypeId, crystal.Quantity, 0, wear.Damage);
            if (wear.Destroyed)
            {
                world.Remove(crystal);
            }
        }
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
        if (extracted + bonus > 0)
        {
            // Use the pinned reference quote, never player orders or the testing market price.
            var quote = MarketSnapshotCatalog.Quotes.GetValueOrDefault(asteroid.TypeId);
            decimal value = quote is null ? 0 : MarketValuationRules.EstimateIsk(quote.BuyCents, quote.SellCents) ?? 0;
            long points = ActivitySkillPointRewards.FromValue(
                value,
                checked(extracted + bonus),
                world.Settings.Progression.MiningSkillPointMultiplier
            );
            if (points > 0)
            {
                PilotTrainingStore.CreditSilentFreePoints(world, pilotId, points);
            }
            PilotTaskTrackingStore.Record(world, catalog, pilotId, now, TaskEvents.Mined);
            PilotTaskTrackingStore.Record(world, catalog, pilotId, now, TaskEvents.TotalMined, extracted + bonus);
        }
        PersonalMiningLedgerStore.Record(world, pilot, asteroid, now, new(checked((long)extracted + bonus), bonus, collected.Residue));
        world.Set<ModuleCooldown>().Where(c => c.ModuleId == moduleId && c.Kind == ModuleCooldownKind.Cycle).ExecuteDelete();
        foreach (var tracked in world.ChangeTracker.Entries<ModuleCooldown>().Where(e => e.Entity.ModuleId == moduleId).ToArray())
        {
            tracked.State = EntityState.Detached;
        }
        world.SaveChanges();
        bool depleted = asteroid.Quantity == 0;
        if (depleted)
        {
            if (MissionMiningRules.IsDeposit(asteroid.Id))
            {
                PilotMissionStore.MiningDepleted(world, asteroid.Id);
            }
            else
            {
                MiningSiteStore.Collected(world, asteroid.Id, now);
            }
            world.SaveChanges();
        }
        return new(change, asteroid.TypeId, extracted, bonus, collected.Residue, depleted, collected.CargoFull, crystalState);
    }

    public void Stop(long pilotId, long moduleId, long expectedCompletesAt)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        Stop(transaction.World, pilotId, moduleId, expectedCompletesAt);
        transaction.Commit();
    }

    internal static void Stop(WorldDbContext world, long pilotId, long moduleId, long expectedCompletesAt)
    {
        world
            .Set<ModuleCooldown>()
            .Where(c =>
                c.ModuleId == moduleId
                && c.Kind == ModuleCooldownKind.Cycle
                && c.ReadyAt == expectedCompletesAt
                && world.Set<OwnedItem>().Any(i => i.Id == moduleId && i.OwnerId == pilotId)
            )
            .ExecuteDelete();
    }
}
