using Eden.Data.World.Mining;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Mining;

public sealed class GasMiningTests
{
    [Fact]
    public void WorldGasYieldUsesItsOwnMultiplierAndKeepsTheResourceFinite()
    {
        using Fixture f = Create();
        Eden.Protocol.Tests.Persistence.WorldTuningTests.Activate(
            f.Db,
            settings => settings with { Resources = settings.Resources with { OreYieldMultiplier = 100, GasYieldMultiplier = 4 } }
        );
        long module = f.Fit(Scoop);
        CloudAtShip(f);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var start = mining.Begin(f.PilotId, module, Cloud, f.Clock.Now, DogmaEffect.MiningClouds);
        Assert.Equal(40, start.Laser.Volume);
        var result = mining.Collect(f.PilotId, module, Cloud, new(1, true, Rolls));
        Assert.Equal(4, result.Extracted);
        Assert.Equal(4, Assert.Single(f.Db.Services.GetRequiredService<IPersonalMiningLedger>().Read(f.PilotId)).Quantity.Collected);
        Assert.Equal(0, result.CriticalBonus);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(96, saved.Set<WorldAsteroid>().Find(Cloud)!.Quantity);
    }

    [Fact]
    public void MiningScannerIdentifiersRejectNegativeSequencesAndUseASeparateRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Eden.Gameplay.Exploration.ScannerTargetIds.FromMiningSequence(-1));
        Assert.NotEqual(
            Eden.Gameplay.Exploration.ScannerTargetIds.FromCombatSequence(0),
            Eden.Gameplay.Exploration.ScannerTargetIds.FromMiningSequence(0)
        );
        Assert.NotEqual(
            Eden.Gameplay.Exploration.ScannerTargetIds.FromExplorationSequence(0),
            Eden.Gameplay.Exploration.ScannerTargetIds.FromMiningSequence(0)
        );
    }

    private const int Scoop = 25266;
    private const int Gas = 28694;
    private const long Cloud = MiningSiteRules.FirstDepositId;
    private static readonly MiningRolls Rolls = new(.99, 0, .99, .99, .99);

    private static Fixture Create()
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadMining(f.CatalogPath);
        return f;
    }

    private static void CloudAtShip(Fixture f, long id = Cloud, int type = Gas)
    {
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new WorldAsteroid
            {
                Id = id,
                SystemId = f.Db.Store.FindPilot(1000001)!.SystemId,
                TypeId = type,
                Quantity = 100,
                X = 1000,
                Radius = 100,
            }
        );
        world.SaveChanges();
    }

    [Fact]
    public void GasHarvestingUsesItsOwnFamilyAndNeverReceivesOreSkillOrCriticalBonuses()
    {
        using Fixture f = Create();
        long module = f.Fit(Scoop);
        foreach (var skill in new[] { MiningSkill.Mining, MiningSkill.Astrogeology, MiningSkill.Precision, MiningSkill.Exploitation })
        {
            f.Train((int)skill, 5);
        }
        CloudAtShip(f);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var start = mining.Begin(f.PilotId, module, Cloud, f.Clock.Now, DogmaEffect.MiningClouds);
        Assert.Equal(MiningFamily.Gas, start.Laser.Family);
        Assert.Equal(10, start.Laser.Volume);
        Assert.Equal(30000, start.Laser.DurationMilliseconds);
        Assert.Equal(0, start.Laser.CriticalChance);
        var result = mining.Collect(f.PilotId, module, Cloud, new(1, true, Rolls));
        Assert.Equal(1, result.Extracted);
        Assert.Equal(0, result.CriticalBonus);
        Assert.Equal(Gas, result.Cargo!.Item.TypeId);
        Assert.Equal((int)InventoryFlag.Cargo, result.Cargo.Item.FlagId);
        WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(99, saved.Set<WorldAsteroid>().Find(Cloud)!.Quantity);
    }

    [Fact]
    public void VentureUsesItsCatalogGasBonusesAndDepositsTheHarvestInItsMiningHold()
    {
        using Fixture f = Create();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.ShipId)!.TypeId = 32880;
            world.SaveChanges();
        }
        f.Train((int)MiningSkill.Frigate, 3);
        long module = f.Fit(Scoop);
        CloudAtShip(f);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var start = mining.Begin(f.PilotId, module, Cloud, f.Clock.Now);
        Assert.Equal(20, start.Laser.Volume, 8);
        Assert.Equal(25500, start.Laser.DurationMilliseconds, 8);
        var result = mining.Collect(f.PilotId, module, Cloud, new(1, true, Rolls));
        Assert.Equal(2, result.Extracted);
        Assert.Equal((int)InventoryFlag.GeneralMiningHold, result.Cargo!.Item.FlagId);
    }

    [Fact]
    public void WrongResourcesAndEffectsRejectBeforeSpendingAndGasSkillLimitsActiveModules()
    {
        using Fixture f = Create();
        long first = f.Fit(Scoop),
            second = f.Fit(Scoop);
        f.Train((int)MiningSkill.GasHarvesting, 1);
        CloudAtShip(f);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var energy = f.Db.Services.GetRequiredService<Eden.Gameplay.Ships.IPilotShipResources>();
        double before = energy.Capacitor(f.PilotId, f.Clock.Now);
        Assert.Throws<InvalidDataException>(() => mining.Begin(f.PilotId, first, Cloud, f.Clock.Now, DogmaEffect.MiningLaser));
        Assert.Equal(before, energy.Capacitor(f.PilotId, f.Clock.Now));
        mining.Begin(f.PilotId, first, Cloud, f.Clock.Now);
        Assert.Throws<InvalidDataException>(() => mining.Begin(f.PilotId, second, Cloud, f.Clock.Now));
        Assert.Equal(0, mining.Collect(f.PilotId, first, Cloud, new(.5, false, Rolls)).Extracted);
        mining.Begin(f.PilotId, second, Cloud, f.Clock.Now);
        mining.Collect(f.PilotId, second, Cloud, new(.5, false, Rolls with { Yield = 0 }));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldAsteroid>().Find(Cloud)!.TypeId = 1230;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => mining.Begin(f.PilotId, first, Cloud, f.Clock.Now));
    }

    [Fact]
    public void FullHoldRejectsBeforeSpendingOrRemovingGas()
    {
        using Fixture f = Create();
        long module = f.Fit(Scoop);
        f.Grant(Gas, 10000, cargo: true);
        CloudAtShip(f);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var energy = f.Db.Services.GetRequiredService<Eden.Gameplay.Ships.IPilotShipResources>();
        double before = energy.Capacitor(f.PilotId, f.Clock.Now);
        Assert.Throws<CargoCapacityException>(() => mining.Begin(f.PilotId, module, Cloud, f.Clock.Now));
        Assert.Equal(before, energy.Capacitor(f.PilotId, f.Clock.Now));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(100, world.Set<WorldAsteroid>().Find(Cloud)!.Quantity);
        Assert.Empty(world.Set<Eden.Data.World.Combat.ModuleCooldown>());
    }

    [Fact]
    public void StaticGasToolsAreAdmittedAndBargeHarvestersRejectAnOrdinaryFrigate()
    {
        using Fixture f = Create();
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            var tools = catalog
                .Records(Eden.World.Catalog.CatalogData.Client.Types)
                .Where(p =>
                    p.Value.Published == 1
                    && p.Value.GroupID is InventoryDefinitions.GasScoopGroup or InventoryDefinitions.GasHarvesterGroup
                    && p.Value.MetaGroupID != 15
                )
                .ToArray();
            Assert.Equal(8, tools.Length);
            Assert.All(tools, p => Assert.NotNull(FittingCatalog.Definition(catalog, checked((int)p.Key))));
            Assert.Null(FittingCatalog.Definition(catalog, 90529));
            Assert.Null(FittingCatalog.Definition(catalog, 90593));
        }
        Assert.Throws<InvalidDataException>(() => f.Fit(60313));
    }

    [Fact]
    public void EmptyDepositsAndExpiredSitesCannotFundAnotherCycle()
    {
        using Fixture f = Create();
        long module = f.Fit(Scoop);
        CloudAtShip(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldAsteroid>().Find(Cloud)!.Quantity = 0;
            world.SaveChanges();
        }
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        Assert.Throws<InvalidDataException>(() => mining.Begin(f.PilotId, module, Cloud, f.Clock.Now));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldAsteroid>().Find(Cloud)!.Quantity = 100;
            world.Add(
                new SavedMiningSite
                {
                    Id = MiningSiteRules.FirstSiteId,
                    SystemId = f.Db.Store.FindPilot(1000001)!.SystemId,
                    Family = MiningFamily.Gas,
                    DungeonId = 2529,
                    ExpiresAt = f.Clock.Now,
                }
            );
            world.Add(new SavedMiningDeposit { Id = Cloud, SiteId = MiningSiteRules.FirstSiteId });
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => mining.Begin(f.PilotId, module, Cloud, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(100, saved.Set<WorldAsteroid>().Find(Cloud)!.Quantity);
        Assert.Empty(saved.Set<Eden.Data.World.Combat.ModuleCooldown>());
    }

    [Fact]
    public void MykoserocinSignaturesPersistFiniteNonSolidCloudsAndReplaceOnlyAfterDepletionDelay()
    {
        using Fixture f = Create();
        int system = f.Db.Store.FindPilot(1000001)!.SystemId;
        var sites = f.Db.Services.GetRequiredService<IMiningSites>();
        var site = Assert.Single(sites.Signatures(system, f.Clock.Now));
        Assert.Equal(MiningFamily.Gas, site.Mining);
        Assert.True(site.IsSignature);
        long[] deposits;
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            deposits = world.Set<SavedMiningDeposit>().Where(d => d.SiteId == site.Id).Select(d => d.Id).ToArray();
            Assert.InRange(deposits.Length, 2, 3);
            Assert.All(world.Set<WorldAsteroid>().Where(a => deposits.Contains(a.Id)), a => Assert.InRange(a.Quantity, 1000, 2000));
            Assert.All(
                AsteroidFieldStore.List(world, catalog, system),
                a =>
                {
                    Assert.Equal(MiningDefinitions.GasCloudGroup, a.GroupId);
                    Assert.False(a.Solid);
                    Assert.False(a.IsGlobal);
                }
            );
        }
        WorldInitialization.Open(f.Db.Path);
        Assert.Equal(site.Id, Assert.Single(new MiningSiteStore(new(f.Db.Path)).Signatures(system, f.Clock.Now)).Id);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldAsteroid>().Where(a => deposits.Contains(a.Id)).ExecuteUpdate(set => set.SetProperty(a => a.Quantity, 0));
            using var transaction = world.Database.BeginTransaction();
            MiningSiteStore.Collected(world, deposits[0], f.Clock.Now);
            world.SaveChanges();
            transaction.Commit();
        }
        Assert.Empty(sites.Signatures(system, f.Clock.Now));
        f.Clock.Milliseconds += (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
        var replacement = Assert.Single(sites.Signatures(system, f.Clock.Now));
        Assert.NotEqual(site.Id, replacement.Id);
        Assert.NotEqual(site.TargetId, replacement.TargetId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.DoesNotContain(saved.Set<WorldAsteroid>(), a => deposits.Contains(a.Id));
        Assert.Equal(
            16,
            MiningSiteCatalog.Sites.Count(s => s.Family == MiningFamily.Gas && !s.LowSecurityOnly && s.WormholeClasses is null)
        );
    }

    [Theory]
    [InlineData(0.4, true)]
    [InlineData(0.5, false)]
    [InlineData(-0.1, false)]
    public void CytoserocinSiteUsesRegionalLowSecurityAndCanBeHarvested(double security, bool expected)
    {
        using Fixture f = Create();
        int system = f.Db.Store.FindPilot(1000001)!.SystemId;
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            string key = system.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string systemRecord = System.Text.Json.JsonSerializer.Serialize(new { security });
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent',{key},{systemRecord})");
        }
        const int banditNebula = 3394;
        const int amberCytoserocin = 25268;
        var gasSites = MiningSiteCatalog.Sites.Where(s => s.LowSecurityOnly).ToArray();
        Assert.Equal(8, gasSites.Length);
        Assert.All(gasSites, s => Assert.Single(s.Regions));
        Assert.Equal(
            new[] { 25268, 25273, 25274, 25275, 25276, 25277, 25278, 25279 },
            gasSites.SelectMany(s => s.Deposits).Select(d => d.TypeId).Distinct().Order()
        );

        long module = f.Fit(Scoop);
        var sites = f.Db.Services.GetRequiredService<IMiningSites>();
        var first = Assert.Single(sites.Signatures(system, f.Clock.Now));
        if (!expected)
        {
            Assert.NotEqual(banditNebula, first.DungeonId);
            return;
        }
        // Rotate the existing finite resource signature through eligible templates.
        for (int i = 0; i < MiningSiteCatalog.Sites.Count && first.DungeonId != banditNebula; i++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(MiningSiteRules.Lifetime + MiningSiteRules.ReplacementDelay));
            first = Assert.Single(sites.Signatures(system, f.Clock.Now));
        }
        Assert.Equal(banditNebula, first.DungeonId);
        WorldAsteroid cloud;
        using (WorldDbContext world = new(f.Db.Path))
        {
            cloud = world.Set<WorldAsteroid>().First(a => a.TypeId == amberCytoserocin && a.SystemId == system);
        }
        f.Db.Store.SetPosition(f.ShipId, new SpacePosition(cloud.X, cloud.Y, cloud.Z));
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var start = mining.Begin(f.PilotId, module, cloud.Id, f.Clock.Now);
        Assert.Equal(MiningFamily.Gas, start.Laser.Family);
        Assert.Equal(amberCytoserocin, mining.Collect(f.PilotId, module, cloud.Id, new(1, true, Rolls)).Cargo!.Item.TypeId);
    }

    [Fact]
    public void WarmGasSignaturesUseNoSqlAndCheckpointKeepsTheSameFiniteDeposits()
    {
        using Fixture f = Create();
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        MiningSiteStore store = new(new(f.Db.Path));
        int system = f.Db.Store.FindPilot(1000001)!.SystemId;
        IReadOnlyList<CombatAnomaly> first;
        // Initialize system deadlines and catalog inputs before measuring site creation.
        LiveWorld.Execute(
            f.Db.Path,
            system,
            () =>
            {
                using var owner = live.Enter();
            },
            "initialize gas site dependencies"
        );
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            _ = catalog.FindSystem(system);
            _ = catalog.Definitions.SystemSecurity;
            foreach (
                int type in MiningSiteCatalog
                    .Sites.Where(site => site.Family == MiningFamily.Gas)
                    .SelectMany(site => site.Deposits)
                    .Select(deposit => deposit.TypeId)
                    .Distinct()
            )
            {
                _ = catalog.FittingType(type);
            }
            _ = catalog.CelestialsInSystem(system).ToArray();
        }
        using (var metrics = SessionPerformance.Begin("first gas signature", _ => { }, force: true)!)
        {
            first = store.Signatures(system, f.Clock.Now);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        Assert.Single(first);
        Assert.Empty(store.Signatures(0, f.Clock.Now));
        using (var metrics = SessionPerformance.Begin("warm mining sites", _ => { }, force: true)!)
        {
            for (int i = 0; i < 20; i++)
            {
                Assert.Same(first, store.Signatures(system, f.Clock.Now + i));
                Assert.Empty(store.Signatures(0, f.Clock.Now + i));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        using (WorldDbContext world = new(f.Db.Path))
        {
            var deposit = world.Set<WorldAsteroid>().OrderBy(a => a.Id).First();
            deposit.Quantity--;
            world.SaveChanges();
        }
        snapshot.Project();
        using WorldDbContext captured = new(snapshot.Database);
        using WorldDbContext current = new(f.Db.Path);
        Assert.Equal(first[0].Id, captured.Set<SavedMiningSite>().Single().Id);
        Assert.Equal(current.Set<WorldAsteroid>().Sum(a => a.Quantity) + 1, captured.Set<WorldAsteroid>().Sum(a => a.Quantity));
    }
}
