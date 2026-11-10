using Eden.Data.World.Mining;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Exploration;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Mining;

public sealed class IceMiningTests
{
    [Fact]
    public void WorldIceYieldScalesWholeCompletedCyclesIndependentlyOfOre()
    {
        using Fixture f = Create();
        Eden.Protocol.Tests.Persistence.WorldTuningTests.Activate(
            f.Db,
            settings => settings with { Resources = settings.Resources with { OreYieldMultiplier = 100, IceYieldMultiplier = 3 } }
        );
        long module = f.Fit(16278);
        RockAtShip(f);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var start = mining.Begin(f.PilotId, module, Rock, f.Clock.Now);
        Assert.Equal(3000, start.Laser.Volume);
        Assert.Equal(0, mining.Collect(f.PilotId, module, Rock, new(.999, false, Rolls)).Extracted);
        mining.Begin(f.PilotId, module, Rock, f.Clock.Now);
        var result = mining.Collect(f.PilotId, module, Rock, new(1, true, Rolls));
        Assert.Equal(3, result.Extracted);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(97, saved.Set<WorldAsteroid>().Find(Rock)!.Quantity);
    }

    private const long Rock = MiningSiteRules.FirstDepositId;
    private const int Ice = 16265;
    private static readonly MiningRolls Rolls = new(.99, .99, .99, .99, .99);

    private static Fixture Create(int hull = 17478)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadMining(f.CatalogPath);
        using WorldDbContext world = new(f.Db.Path);
        world.Ships.Find(f.ShipId)!.TypeId = hull;
        world.SaveChanges();
        return f;
    }

    private static void RockAtShip(Fixture f, int type = Ice)
    {
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new WorldAsteroid
            {
                Id = Rock,
                SystemId = f.Db.Store.FindPilot(1000001)!.SystemId,
                TypeId = type,
                Quantity = 100,
                X = 1000,
                Radius = 100,
            }
        );
        world.SaveChanges();
    }

    private static void AddTestSystem(Fixture f, int system)
    {
        // The small test catalog has only starter geography. Give the admitted
        // real system an inert anchor; production reads its pinned geography.
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        catalog.Add(
            new Eden.Data.Catalog.SolarSystem
            {
                Id = system,
                Name = "Ice field test system",
                ConstellationId = catalog.Set<Eden.Data.Catalog.Constellation>().First().Id,
            }
        );
        var anchor = catalog.Set<Eden.Data.Catalog.Celestial>().AsNoTracking().First();
        anchor.Id = system + 20000000;
        anchor.SolarSystemId = system;
        catalog.Add(anchor);
        catalog.SaveChanges();
    }

    [Fact]
    public void IceNeedsACompleteCycleAndUsesNativeCycleAndCriticalSkillsWithoutOreYieldBonuses()
    {
        using Fixture f = Create();
        long module = f.Fit(16278);
        f.Train((int)MiningSkill.IceHarvesting, 3);
        foreach (var skill in new[] { MiningSkill.Mining, MiningSkill.Astrogeology, MiningSkill.Precision, MiningSkill.Exploitation })
        {
            f.Train((int)skill, 5);
        }
        RockAtShip(f);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var first = mining.Begin(f.PilotId, module, Rock, f.Clock.Now);
        Assert.Equal(MiningFamily.Ice, first.Laser.Family);
        Assert.Equal(DogmaEffect.MiningLaser, first.Laser.Effect);
        Assert.Equal(1000, first.Laser.Volume);
        // Retriever role bonus (-12.5%) and Ice Harvesting III (-15%) both shorten the cycle.
        Assert.Equal(178500, first.Laser.DurationMilliseconds, 8);
        Assert.Equal(.015, first.Laser.CriticalChance, 8);
        Assert.Equal(2.5, first.Laser.CriticalBonusYield, 8);
        var partial = mining.Collect(f.PilotId, module, Rock, new(.999, false, Rolls with { Yield = 0, Critical = 0, Residue = 0 }));
        Assert.Equal(0, partial.Extracted);
        Assert.Equal(0, partial.CriticalBonus);
        Assert.Equal(0, partial.Residue);
        Assert.Null(partial.Cargo);
        mining.Begin(f.PilotId, module, Rock, f.Clock.Now);
        var complete = mining.Collect(f.PilotId, module, Rock, new(1, true, Rolls));
        Assert.Equal(1, complete.Extracted);
        Assert.Equal(0, complete.CriticalBonus);
        Assert.Equal(Ice, complete.Cargo!.Item.TypeId);
        Assert.Equal((int)InventoryFlag.GeneralMiningHold, complete.Cargo.Item.FlagId);
        WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(99, saved.Set<WorldAsteroid>().Find(Rock)!.Quantity);
        Assert.Throws<InvalidDataException>(() => mining.Collect(f.PilotId, module, Rock, new(1, true, Rolls)));
    }

    [Fact]
    public void IceProfilesAreAdmittedAndHullResourceAndWholeBlockCapacityRestrictionsAreAtomic()
    {
        using Fixture f = Create(601);
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            foreach (int type in new[] { 16278, 22229, 28752, 37450, 37451, 37452 })
            {
                Assert.NotNull(Eden.World.Fittings.FittingCatalog.Definition(catalog, type));
            }
            Assert.Null(Eden.World.Fittings.FittingCatalog.Definition(catalog, 90502));
            Assert.Null(Eden.World.Fittings.FittingCatalog.Definition(catalog, 90524));
        }
        Assert.Throws<InvalidDataException>(() => f.Fit(37450));
        Assert.Throws<InvalidDataException>(() => f.Fit(16278));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.ShipId)!.TypeId = 17478;
            world.SaveChanges();
        }
        long iceModule = f.Fit(16278),
            oreModule = f.Fit(17482);
        RockAtShip(f);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var energy = f.Db.Services.GetRequiredService<Eden.Gameplay.Ships.IPilotShipResources>();
        double before = energy.Capacitor(f.PilotId, f.Clock.Now);
        Assert.Throws<InvalidDataException>(() => mining.Begin(f.PilotId, oreModule, Rock, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => mining.Begin(f.PilotId, iceModule, Rock, f.Clock.Now, DogmaEffect.MiningClouds));
        foreach (int unsupported in new[] { 1230, 28444 })
        {
            using WorldDbContext world = new(f.Db.Path);
            world.Set<WorldAsteroid>().Find(Rock)!.TypeId = unsupported;
            world.SaveChanges();
            Assert.Throws<InvalidDataException>(() => mining.Begin(f.PilotId, iceModule, Rock, f.Clock.Now));
        }
        Assert.Equal(before, energy.Capacitor(f.PilotId, f.Clock.Now));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldAsteroid>().Find(Rock)!.TypeId = Ice;
            world.SaveChanges();
        }
        long cargo = f.Grant(Ice, 10000, cargo: true);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<Eden.Data.World.Inventory.OwnedItem>().Find(cargo)!.FlagId = (int)InventoryFlag.GeneralMiningHold;
            world.SaveChanges();
        }
        Assert.Throws<CargoCapacityException>(() => mining.Begin(f.PilotId, iceModule, Rock, f.Clock.Now));
        Assert.Equal(before, energy.Capacitor(f.PilotId, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(100, saved.Set<WorldAsteroid>().Find(Rock)!.Quantity);
        Assert.Empty(saved.Set<Eden.Data.World.Combat.ModuleCooldown>());
    }

    [Theory]
    [InlineData(16278, 17478, 10000)]
    [InlineData(22229, 17478, 10000)]
    [InlineData(28752, 17478, 12500)]
    [InlineData(37450, 91174, 7000)]
    [InlineData(37451, 91174, 8000)]
    [InlineData(37452, 91174, 11000)]
    public void EachStaticIceToolFitsItsNativeHullAndCollectsWholeBlocks(int type, int hull, double range)
    {
        using Fixture f = Create(hull);
        long module = f.Fit(type);
        RockAtShip(f);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var cycle = mining.Begin(f.PilotId, module, Rock, f.Clock.Now);
        Assert.Equal(MiningFamily.Ice, cycle.Laser.Family);
        Assert.Equal(range, cycle.Laser.Range);
        Assert.Equal(1000, cycle.Laser.Volume);
        var result = mining.Collect(f.PilotId, module, Rock, new(1, true, Rolls));
        Assert.Equal(1, result.Extracted);
        Assert.Equal(0, result.Residue);
        Assert.Equal(1, result.Cargo!.Item.Quantity);
    }

    [Theory]
    [InlineData(5496, 21)]
    [InlineData(5497, 19)]
    [InlineData(5498, 20)]
    [InlineData(5499, 19)]
    public void PublicIceFieldsPersistFiniteSolidRocksAndRespawnOnlySixHoursAfterDepletion(int dungeon, int count)
    {
        using Fixture f = Create();
        var definition = MiningSiteCatalog.Sites.Single(s => s.DungeonId == dungeon);
        int system = definition.Systems![0];
        AddTestSystem(f, system);
        var sites = f.Db.Services.GetRequiredService<IMiningSites>();
        var site = Assert.Single(sites.Anomalies(system, f.Clock.Now));
        Assert.Equal(dungeon, site.DungeonId);
        Assert.Equal(MiningFamily.Ice, site.Mining);
        Assert.False(site.IsSignature);
        var wire = Assert.IsType<BlueInstance>(new AnomalySiteInfo(site).ToBlue());
        Assert.Equal(MiningSiteRules.IceArchetype, Assert.IsType<Dictionary<string, object?>>(wire.State)["archetypeID"]);
        var publicSites = f.Db.Services.GetRequiredService<ICombatAnomalies>().List(system, f.Clock.Now);
        Assert.Contains(publicSites, s => s.Id == site.Id);
        Assert.DoesNotContain(sites.Signatures(system, f.Clock.Now), s => s.Id == site.Id);
        long[] ids;
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            ids = world.Set<SavedMiningDeposit>().Where(d => d.SiteId == site.Id).Select(d => d.Id).ToArray();
            Assert.Equal(count, ids.Length);
            Assert.All(world.Set<WorldAsteroid>().Where(a => ids.Contains(a.Id)), a => Assert.InRange(a.Quantity, 1, 400));
            Assert.All(AsteroidFieldStore.List(world, catalog, system).Where(a => ids.Contains(a.Id)), a => Assert.True(a.Solid));
        }
        WorldInitialization.Open(f.Db.Path);
        Assert.Equal(site.Id, Assert.Single(new MiningSiteStore(new(f.Db.Path)).Anomalies(system, f.Clock.Now)).Id);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldAsteroid>().Where(a => ids.Contains(a.Id)).ExecuteUpdate(s => s.SetProperty(a => a.Quantity, 0));
            using var transaction = world.Database.BeginTransaction();
            MiningSiteStore.Collected(world, ids[0], f.Clock.Now);
            world.SaveChanges();
            transaction.Commit();
        }
        Assert.Empty(sites.Anomalies(system, f.Clock.Now + MiningSiteRules.IceReplacementDelay - 1));
        var next = Assert.Single(sites.Anomalies(system, f.Clock.Now + MiningSiteRules.IceReplacementDelay));
        Assert.NotEqual(site.Id, next.Id);
        Assert.NotEqual(site.TargetId, next.TargetId);
        Assert.NotEqual(site.Position, next.Position);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.DoesNotContain(saved.Set<WorldAsteroid>(), a => ids.Contains(a.Id));
    }

    [Fact]
    public void WarmIceAndGasViewsAreSeparateAndCheckpointKeepsTheirFiniteStock()
    {
        using Fixture f = Create();
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        MiningSiteStore store = new(new(f.Db.Path));
        int system = MiningSiteCatalog.Sites.Single(s => s.DungeonId == 5497).Systems![0];
        AddTestSystem(f, system);
        _ = store.Anomalies(system, f.Clock.Now);
        var gas = store.Signatures(system, f.Clock.Now);
        var ice = store.Anomalies(system, f.Clock.Now);
        Assert.Single(ice);
        Assert.NotSame(ice, gas);
        Assert.Empty(store.Anomalies(0, f.Clock.Now));
        using (var metrics = SessionPerformance.Begin("warm ice and gas", _ => { }, force: true)!)
        {
            for (int i = 0; i < 20; i++)
            {
                Assert.Same(ice, store.Anomalies(system, f.Clock.Now + i));
                Assert.Same(gas, store.Signatures(system, f.Clock.Now + i));
                Assert.Empty(store.Anomalies(0, f.Clock.Now + i));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldAsteroid>().OrderBy(a => a.Id).First().Quantity--;
            world.SaveChanges();
        }
        snapshot.Project();
        using WorldDbContext captured = new(snapshot.Database);
        using WorldDbContext current = new(f.Db.Path);
        Assert.Equal(current.Set<WorldAsteroid>().Sum(a => a.Quantity) + 1, captured.Set<WorldAsteroid>().Sum(a => a.Quantity));
    }
}
