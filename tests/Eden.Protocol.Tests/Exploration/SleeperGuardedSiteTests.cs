using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Mining;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Exploration;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class SleeperGuardedSiteTests
{
    private static int Prepare(Fixture f, int level, bool hacking = false)
    {
        if (hacking)
        {
            PassiveCatalogFixture.LoadHacking(f.CatalogPath);
        }
        PassiveCatalogFixture.LoadWormholes(f.CatalogPath);
        int system;
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            system = checked((int)catalog.Records(CatalogData.Client.Systems).First(s => s.Value.WormholeClassID == level).Key);
        }
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            long celestial = 50_000_000 + level;
            catalog.Database.ExecuteSql(
                $"INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES ({celestial},{system},52673,8,2000,0,0,0)"
            );
        }
        return system;
    }

    private static void KillWave(Fixture f, LiveWorld live, long encounter, int system)
    {
        using var transaction = live.Economy.Begin(new(systems: [system], pilots: [f.PilotId]));
        using CatalogDbContext catalog = new(f.CatalogPath);
        var pilot = transaction.Pilots.Find(f.PilotId)!;
        var alive = transaction.Filter<WorldNpc>(n => n.EncounterId == encounter && n.DestroyedAt == null && n.RetiredAt == null).ToArray();
        Assert.NotEmpty(alive);
        foreach (var npc in alive)
        {
            var result = NpcDamagePersistence.Apply(
                transaction,
                catalog,
                npc,
                pilot,
                pilot.ActiveShipId,
                new NpcProfileCatalog().Read(catalog, npc.TypeId).Attributes,
                new(1_000_000_000, 0, 0, 0),
                f.Clock.Now,
                bypassResistances: true
            );
            Assert.True(result.Destroyed);
            Assert.Null(result.Reward);
        }
        transaction.Commit();
    }

    [Theory]
    [InlineData(3555, 1)]
    [InlineData(3556, 1)]
    [InlineData(3557, 1)]
    [InlineData(3558, 1)]
    [InlineData(3559, 1)]
    [InlineData(3560, 3)]
    [InlineData(3561, 3)]
    [InlineData(3562, 5)]
    [InlineData(3563, 5)]
    public async Task FulleriteDefendersUseSavedWarpDeadlineAndNeverRepeatAfterRestart(int dungeon, int level)
    {
        using Fixture f = new();
        int system = Prepare(f, level);
        long parent;
        long encounter;
        long due;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            MiningSiteStore mining = new(new(f.Db.Path));
            var site = Assert.Single(mining.Signatures(system, f.Clock.Now));
            int rotations = 0;
            while (site.DungeonId != dungeon)
            {
                Assert.True(rotations++ < 9);
                using (var transaction = live.Economy.Begin(new(systems: [system])))
                {
                    foreach (var deposit in transaction.Filter<SavedMiningDeposit>(d => d.SiteId == site.Id))
                    {
                        transaction.Set<WorldAsteroid>().Find(deposit.Id)!.Quantity = 0;
                    }
                    var first = transaction.Filter<SavedMiningDeposit>(d => d.SiteId == site.Id).First();
                    MiningSiteStore.Collected(transaction, first.Id, f.Clock.Now);
                    transaction.Commit();
                }
                f.Clock.AdvanceWallTime(TimeSpan.FromTicks(MiningSiteRules.ReplacementDelay));
                site = Assert.Single(mining.Signatures(system, f.Clock.Now));
            }
            parent = site.Id;
            encounter = live.Economy.Inspect(world => world.Filter<SavedSleeperSite>(s => s.ParentSiteId == parent).Single().Id);
            Assert.Empty(live.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).ToArray()));
            CombatAnomalyStore sites = new(new(f.Db.Path), mining: mining);
            sites.Activate(system, site.Position, f.Clock.Now);
            due = live.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.NextWaveAt!.Value);
            Assert.InRange(due - f.Clock.Now, 900 * TimeSpan.TicksPerSecond, 1500 * TimeSpan.TicksPerSecond);
            sites.Activate(system, site.Position, f.Clock.Now + 1);
            Assert.Equal(due, live.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.NextWaveAt));
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            MiningSiteStore mining = new(new(f.Db.Path));
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(due - f.Clock.Now - 1));
            Assert.Single(mining.Signatures(system, f.Clock.Now));
            Assert.Empty(restarted.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).ToArray()));
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
            var site = Assert.Single(mining.Signatures(system, f.Clock.Now));
            Assert.NotEmpty(restarted.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).ToArray()));
            CombatAnomalyStore sites = new(new(f.Db.Path), mining: mining);
            sites.Arrive(system, site.Position, f.Clock.Now);
            site = Assert.Single(mining.Signatures(system, f.Clock.Now));
            using (var metrics = SessionPerformance.Begin("warm guarded reservoir", _ => { }, force: true)!)
            {
                for (int i = 0; i < 20; i++)
                {
                    sites.Arrive(system, site.Position, f.Clock.Now);
                    Assert.Same(site, Assert.Single(mining.Signatures(system, f.Clock.Now)));
                }
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            int waves = 0;
            while (!restarted.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.CompletedAt.HasValue))
            {
                Assert.True(waves++ < 3);
                KillWave(f, restarted, encounter, system);
            }
            Assert.Equal(parent, Assert.Single(mining.Signatures(system, f.Clock.Now)).Id);
            Assert.True(restarted.Economy.Inspect(world => world.Filter<WorldAsteroid>(a => a.SystemId == system && a.Quantity > 0).Any()));
            new CombatAnomalyStore(new(f.Db.Path), mining: mining).Arrive(system, site.Position, f.Clock.Now);
            Assert.Empty(
                restarted.Economy.Inspect(world =>
                    world.Filter<WorldNpc>(n => n.EncounterId == encounter && n.DestroyedAt == null).ToArray()
                )
            );
        }
    }

    private static void Win(PirateExplorationStore store, Fixture f, LiveWorld live)
    {
        using (var transaction = live.Economy.Begin(new(pilots: [f.PilotId])))
        {
            var attempt = transaction.Set<SavedHackingAttempt>().Find(f.PilotId)!;
            var contact = JsonSerializer.Deserialize<HackingContact>(attempt.ContactJson)!;
            HackingBoard board = new(
                [
                    new(2, new(0, 0), HackingNode.Segment, HackingSubsystem.None, Hidden: false),
                    new(3, new(1, 0), HackingNode.Core, HackingSubsystem.LowCore, 20, 10, false),
                ],
                [],
                90,
                25,
                25
            );
            attempt.ContactJson = JsonSerializer.Serialize(contact with { Board = board });
            transaction.Commit();
        }
        Assert.Equal(HackingOutcome.Won, store.Act(f.PilotId, new(HackingActionKind.Click, new(1, 0)), f.Clock.Now).Current.Outcome);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task GuardedHackingUsesNativeAnalyzersAndPersistsResearchLootWithoutDuplication(int level)
    {
        using Fixture f = new();
        int system = Prepare(f, level, hacking: true);
        long dataAnalyzer = f.Fit(22175);
        long relicAnalyzer = f.Fit(22177);
        List<long> hacked = [];
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var store = (PirateExplorationStore)f.Db.Services.GetRequiredService<IPirateExploration>();
            CombatAnomalyStore sites = new(new(f.Db.Path), store);
            var signatures = store.Signatures(system, f.Clock.Now);
            Assert.Equal(2, signatures.Count(s => s.FactionId == SleeperRules.FactionId));
            foreach (var site in signatures.Where(s => s.FactionId == SleeperRules.FactionId))
            {
                var container = site.Objects.First(o => HackingRules.IsContainer(o.Id) && !SleeperRules.IsTalocan(o.TypeId));
                f.Db.Store.SetPosition(f.ShipId, container.Position, system);
                sites.Arrive(system, site.Position, f.Clock.Now);
                long encounter = live.Economy.Inspect(world => world.Filter<SavedSleeperSite>(s => s.ParentSiteId == site.Id).Single().Id);
                Assert.NotEmpty(live.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).ToArray()));
                long analyzer = site.Exploration == ExplorationKind.Data ? dataAnalyzer : relicAnalyzer;
                Assert.Throws<InvalidDataException>(() => store.Begin(f.PilotId, analyzer, container.Id, new HashSet<long>(), f.Clock.Now));
                Assert.Equal(0, live.Economy.Inspect(world => world.Set<SavedSleeperSite>().Find(encounter)!.HackStarts));
                store.Begin(f.PilotId, analyzer, container.Id, new HashSet<long> { container.Id }, f.Clock.Now);
                Win(store, f, live);
                var loot = store.Read(f.PilotId, container.Id, f.Clock.Now).Items;
                Assert.NotEmpty(loot);
                Assert.All(loot, item => Assert.True(Enum.IsDefined((SleeperResearchMaterial)item.TypeId)));
                Assert.Throws<InvalidDataException>(() => store.Act(f.PilotId, new(HackingActionKind.Click, new(1, 0)), f.Clock.Now));
                Assert.Equal(loot, store.Read(f.PilotId, container.Id, f.Clock.Now).Items);
                hacked.Add(container.Id);
            }
            var warm = store.Signatures(system, f.Clock.Now);
            foreach (var site in warm)
            {
                sites.Arrive(system, site.Position, f.Clock.Now);
            }
            warm = store.Signatures(system, f.Clock.Now);
            using (var metrics = SessionPerformance.Begin("warm guarded hacking sites", _ => { }, force: true)!)
            {
                foreach (var site in warm)
                {
                    sites.Arrive(system, site.Position, f.Clock.Now);
                }
                Assert.Same(warm, store.Signatures(system, f.Clock.Now));
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            Assert.All(hacked, id => Assert.True(restarted.Economy.Inspect(world => world.Set<SavedHackingContainer>().Find(id)!.Hacked)));
        }
    }

    [Fact]
    public void AllGuardedReportsBindNativeContainersAndConditionalReinforcements()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadWormholes(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var definitions = SleeperSiteCatalog.Read(catalog);
        Assert.Equal(57, definitions.Length);
        Assert.Equal(24, definitions.Count(d => d.WormholeKind == WormholeSiteKind.Combat));
        Assert.Equal(12, definitions.Count(d => d.WormholeKind == WormholeSiteKind.Data));
        Assert.Equal(12, definitions.Count(d => d.WormholeKind == WormholeSiteKind.Relic));
        Assert.Equal(9, definitions.Count(d => d.WormholeKind == WormholeSiteKind.Gas));
        Assert.All(
            definitions.SelectMany(d => d.Waves).SelectMany(w => w.Groups).SelectMany(g => g.Types).Distinct(),
            type => Assert.NotNull(SleeperLootCatalog.Drops(type))
        );
        var hacking = SleeperSiteCatalog.Exploration(catalog);
        Assert.Equal(24, hacking.Length);
        Assert.All(
            definitions.Where(d => d.Waves.SelectMany(w => w.Groups).Any(g => g.SpawnCondition == SleeperSpawnCondition.Talocan)),
            d => Assert.Contains(hacking.Single(h => h.DungeonId == d.DungeonId).Containers, c => SleeperRules.IsTalocan(c.TypeId))
        );
        Assert.All(
            hacking,
            d =>
            {
                Assert.InRange(d.Containers.Count, 1, HackingRules.ContainersPerSite - 1);
                Assert.All(
                    d.Containers,
                    c =>
                    {
                        Assert.Contains(
                            catalog.FittingType(c.TypeId)!.GroupId,
                            new[] { HackingRules.ContainerGroup, SleeperRules.RelicContainerGroup }
                        );
                        if (SleeperRules.IsTalocan(c.TypeId))
                        {
                            Assert.True(c.Optional);
                            Assert.Equal(ExplorationKind.Relic, c.AnalyzerKind);
                        }
                    }
                );
            }
        );
        Assert.Equal(
            SleeperWaveTrigger.HackStarted,
            definitions.Single(d => d.Name == "Unsecured Frontier Digital Nexus").Waves[1].Trigger
        );
        Assert.Equal(SleeperWaveTrigger.HackFailed, definitions.Single(d => d.Name == "Unsecured Frontier Trinary Hub").Waves[1].Trigger);
        Assert.Equal(
            SleeperWaveTrigger.HackFailed,
            definitions.Single(d => d.Name == "Forgotten Frontier Conversion Module").Waves[1].Trigger
        );
        Assert.Equal(SleeperWaveTrigger.HackResolved, definitions.Single(d => d.Name == "Forgotten Core Information Pen").Waves[2].Trigger);
        Assert.Contains(
            definitions.Single(d => d.Name == "Unsecured Frontier Receiver").Waves.SelectMany(w => w.Groups),
            g => g.SpawnCondition == SleeperSpawnCondition.Aggression
        );
        Assert.All(
            definitions.Where(d => d.WormholeKind == WormholeSiteKind.Gas),
            d =>
            {
                Assert.Equal(900, d.ArrivalDelayMinimum);
                Assert.Equal(1500, d.ArrivalDelayMaximum);
            }
        );
        for (int level = 1; level <= 6; level++)
        {
            foreach (var kind in new[] { ExplorationKind.Data, ExplorationKind.Relic })
            {
                for (int seed = 0; seed < 100; seed++)
                {
                    var rewards = SleeperExplorationLoot.Roll(seed, level, kind, 0);
                    Assert.NotEmpty(rewards);
                    Assert.All(
                        rewards,
                        reward =>
                        {
                            Assert.True(Enum.IsDefined((SleeperResearchMaterial)reward.TypeId));
                            Assert.InRange(reward.Quantity, 1, 6);
                            Assert.Equal(1, catalog.Require(CatalogData.Client.Types, (long)reward.TypeId).Published);
                        }
                    );
                }
            }
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => SleeperExplorationLoot.Roll(0, 0, ExplorationKind.Data, 0));
    }

    private static CombatAnomaly Choose(PirateExplorationStore store, Fixture f, LiveWorld live, int system, string name)
    {
        using CatalogDbContext catalog = new(f.CatalogPath);
        var definition = SleeperSiteCatalog.Exploration(catalog).Single(d => d.Name == name);
        var site = store.Signatures(system, f.Clock.Now).Single(s => s.Exploration == definition.Kind);
        if (site.DungeonId != definition.DungeonId)
        {
            long expiry = live.Economy.Inspect(world => world.Set<SavedExplorationSite>().Find(site.Id)!.ExpiresAt);
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(expiry - f.Clock.Now));
            Assert.DoesNotContain(store.Signatures(system, f.Clock.Now), s => s.Id == site.Id);
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(HackingRules.ReplacementDelay));
            site = store.Signatures(system, f.Clock.Now).Single(s => s.Exploration == definition.Kind);
        }
        Assert.Equal(definition.DungeonId, site.DungeonId);
        return site;
    }

    private static PirateExplorationStore NewStore(Fixture f) =>
        new(
            new(f.Db.Path),
            f.Db.Store,
            f.Db.Services.GetRequiredService<Eden.World.Combat.PilotLossSettlement>(),
            f.Db.Services.GetRequiredService<Eden.Gameplay.Combat.IPilotDamageEvents>()
        );

    [Fact]
    public async Task StartupQueuesAbandonedHackAndResidentRecoverySettlesItsReinforcementsExactlyOnce()
    {
        using Fixture f = new();
        int system = Prepare(f, 4, hacking: true);
        long analyzer = f.Fit(22175);
        long encounter;
        long cache;
        long ready;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var store = NewStore(f);
            var site = Choose(store, f, live, system, "Unsecured Frontier Trinary Hub");
            new CombatAnomalyStore(new(f.Db.Path), store).Arrive(system, site.Position, f.Clock.Now);
            encounter = live.Economy.Inspect(world => CombatAnomalyStore.Guarded(world, site.Id)!.Id);
            while (live.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter && n.DestroyedAt == null).Any()))
            {
                KillWave(f, live, encounter, system);
            }
            var container = site.Objects.First(o => HackingRules.IsContainer(o.Id) && !SleeperRules.IsTalocan(o.TypeId));
            cache = container.Id;
            f.Db.Store.SetPosition(f.ShipId, container.Position, system);
            ready = store.Begin(f.PilotId, analyzer, cache, new HashSet<long> { cache }, f.Clock.Now).ReadyAt;
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.True(world.Set<SavedHackingAttempt>().Find(f.PilotId)!.RecoveryPending);
            Assert.Equal(0, world.Set<SavedHackingContainer>().Find(cache)!.Failures);
            Assert.Equal(0, world.Set<WorldCombatAnomaly>().Find(encounter)!.Wave);
        }
        int count;
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var store = NewStore(f);
            using (CatalogDbContext catalog = new(f.CatalogPath))
            {
                foreach (
                    int type in SleeperSiteCatalog
                        .Read(catalog)
                        .SelectMany(d => d.Waves)
                        .SelectMany(w => w.Groups)
                        .SelectMany(g => g.Types)
                        .Distinct()
                )
                {
                    _ = new NpcProfileCatalog().Read(catalog, type);
                }
            }
            List<string> commands = [];
            using (
                var metrics = SessionPerformance.Begin("resident abandoned-hack recovery", _ => { }, force: true, command: commands.Add)!
            )
            {
                _ = store.Signatures(system, f.Clock.Now);
                Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            store.Cancel(f.PilotId, f.Clock.Now);
            _ = store.Signatures(system, f.Clock.Now);
            Assert.Equal(1, restarted.Economy.Inspect(world => world.Set<SavedHackingContainer>().Find(cache)!.Failures));
            Assert.Equal(1, restarted.Economy.Inspect(world => world.Set<SavedSleeperSite>().Find(encounter)!.HackFailures));
            Assert.Equal(1, restarted.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.Wave));
            Assert.Null(restarted.Economy.Inspect(world => world.Set<SavedHackingAttempt>().Find(f.PilotId)));
            Assert.Equal(ready, restarted.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(analyzer)!.ReadyAt));
            count = restarted.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).Count());
            await restarted.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using (LiveWorld again = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            _ = NewStore(f).Signatures(system, f.Clock.Now);
            Assert.Equal(1, again.Economy.Inspect(world => world.Set<SavedHackingContainer>().Find(cache)!.Failures));
            Assert.Equal(count, again.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).Count()));
        }
    }

    [Fact]
    public async Task Version171CombatSiteAcquiresResidentDeadlineWithoutResettingItsWave()
    {
        using Fixture f = new();
        int system = Prepare(f, 1);
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            int dungeon = SleeperSiteCatalog
                .Read(catalog)
                .First(d => d.WormholeClass == 1 && d.WormholeKind == WormholeSiteKind.Combat)
                .DungeonId;
            world.Add(
                new WorldCombatAnomaly
                {
                    Id = CombatAnomalyRules.FirstSiteId,
                    SystemId = system,
                    DungeonId = dungeon,
                    DefinitionVersion = AnomalyEncounterRules.CurrentDefinitionVersion,
                    Wave = 1,
                }
            );
            world.SaveChanges();
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldSleeperSite; ALTER TABLE WorldHackingAttempt DROP COLUMN RecoveryPending; PRAGMA user_version=171"
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        long expiry;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            Assert.Empty(live.Economy.Inspect(world => world.Filter<SavedSleeperSite>(_ => true).ToArray()));
            var site = new CombatAnomalyStore(new(f.Db.Path)).List(system, f.Clock.Now).Single(s => s.Id == CombatAnomalyRules.FirstSiteId);
            Assert.True(site.Started);
            Assert.Equal(1, live.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(site.Id)!.Wave));
            expiry = live.Economy.Inspect(world => world.Set<SavedSleeperSite>().Find(site.Id)!.ExpiresAt!.Value);
            Assert.Equal(f.Clock.Now + SleeperRules.SiteLifetime, expiry);
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            Assert.Equal(
                expiry,
                restarted.Economy.Inspect(world => world.Set<SavedSleeperSite>().Find(CombatAnomalyRules.FirstSiteId)!.ExpiresAt)
            );
            Assert.Equal(1, restarted.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(CombatAnomalyRules.FirstSiteId)!.Wave));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedSiteExpiryRetiresHostilesAndPreventsLateDefenderSpawns(bool gas)
    {
        using Fixture f = new();
        int system = Prepare(f, 1);
        long parent;
        long encounter;
        long expiry;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            MiningSiteStore mining = new(new(f.Db.Path));
            CombatAnomalyStore sites = new(new(f.Db.Path), mining: mining);
            var site = gas ? Assert.Single(mining.Signatures(system, f.Clock.Now)) : sites.List(system, f.Clock.Now)[0];
            parent = site.Id;
            if (gas)
            {
                sites.Activate(system, site.Position, f.Clock.Now);
                encounter = live.Economy.Inspect(world => CombatAnomalyStore.Guarded(world, parent)!.Id);
            }
            else
            {
                sites.Arrive(system, site.Position, f.Clock.Now);
                encounter = parent;
            }
            expiry = live.Economy.Inspect(world => world.Set<SavedSleeperSite>().Find(encounter)!.ExpiresAt!.Value);
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(expiry - f.Clock.Now));
            MiningSiteStore mining = new(new(f.Db.Path));
            CombatAnomalyStore sites = new(new(f.Db.Path), mining: mining);
            var current = gas ? mining.Signatures(system, f.Clock.Now) : sites.List(system, f.Clock.Now);
            Assert.DoesNotContain(current, s => s.Id == parent);
            Assert.Equal(expiry, restarted.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.CompletedAt));
            var npcs = restarted.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).ToArray());
            if (gas)
            {
                Assert.Empty(npcs);
            }
            else
            {
                Assert.NotEmpty(npcs);
                Assert.All(npcs, npc => Assert.Equal(expiry, npc.RetiredAt));
            }
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(CombatAnomalyRules.ReplacementSeconds));
            var replaced = gas ? mining.Signatures(system, f.Clock.Now) : sites.List(system, f.Clock.Now);
            Assert.DoesNotContain(replaced, s => s.Id == parent);
            Assert.Equal(gas ? 1 : 4, replaced.Count);
        }
    }

    [Fact]
    public async Task OptionalTalocanCacheRequiresRelicAnalyzerAndKeepsItsGuardAndHullLootAcrossRestart()
    {
        using Fixture f = new();
        int system = Prepare(f, 4, hacking: true);
        long data = f.Fit(22175);
        long relic = f.Fit(22177);
        long parent = Enumerable.Range(0, 200).Select(i => HackingRules.FirstSiteId + i).First(SleeperRules.HasTalocan);
        long encounter;
        long talocan;
        int npcs;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            using (CatalogDbContext catalog = new(f.CatalogPath))
            using (var transaction = live.Economy.Begin(new(systems: [system])))
            {
                var definition = SleeperSiteCatalog.Exploration(catalog).Single(d => d.Name == "Unsecured Frontier Digital Nexus");
                transaction.Add(
                    new SavedExplorationSite
                    {
                        Id = parent,
                        SystemId = system,
                        Kind = (int)definition.Kind,
                        DungeonId = definition.DungeonId,
                        ExpiresAt = f.Clock.Now + SleeperRules.SiteLifetime,
                    }
                );
                for (int index = 0; index < definition.Containers.Count; index++)
                {
                    transaction.Add(
                        new SavedHackingContainer
                        {
                            Id =
                                HackingRules.FirstContainerId
                                + (parent - HackingRules.FirstSiteId) * HackingRules.ContainersPerSite
                                + index,
                            SiteId = parent,
                            Index = index,
                            LootSeed = index,
                        }
                    );
                }
                encounter = CombatAnomalyStore
                    .EnsureGuarded(transaction, catalog, parent, system, definition.DungeonId, SpacePosition.Zero)
                    .Id;
                transaction.Commit();
            }
            var store = (PirateExplorationStore)f.Db.Services.GetRequiredService<IPirateExploration>();
            var site = store.Signatures(system, f.Clock.Now).Single(s => s.Id == parent);
            var cache = Assert.Single(site.Objects, o => HackingRules.IsContainer(o.Id) && SleeperRules.IsTalocan(o.TypeId));
            talocan = cache.Id;
            new CombatAnomalyStore(new(f.Db.Path), store).Arrive(system, site.Position, f.Clock.Now);
            using (CatalogDbContext catalog = new(f.CatalogPath))
            {
                int guard = CombatAnomalyCatalog
                    .Find(catalog, site.DungeonId)!
                    .Waves[0]
                    .Groups.Single(g => g.SpawnCondition == SleeperSpawnCondition.Talocan)
                    .Types.Single();
                Assert.Single(
                    live.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter && n.TypeId == guard).ToArray())
                );
            }
            f.Db.Store.SetPosition(f.ShipId, cache.Position, system);
            Assert.Throws<InvalidDataException>(() => store.Begin(f.PilotId, data, talocan, new HashSet<long> { talocan }, f.Clock.Now));
            Assert.Equal(ExplorationKind.Relic, store.Begin(f.PilotId, relic, talocan, new HashSet<long> { talocan }, f.Clock.Now).Kind);
            Win(store, f, live);
            var loot = Assert.Single(store.Read(f.PilotId, talocan, f.Clock.Now).Items);
            Assert.Contains(
                (SleeperResearchMaterial)loot.TypeId,
                new[]
                {
                    SleeperResearchMaterial.IntactHullSection,
                    SleeperResearchMaterial.MalfunctioningHullSection,
                    SleeperResearchMaterial.WreckedHullSection,
                }
            );
            npcs = live.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).Count());
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var store = (PirateExplorationStore)f.Db.Services.GetRequiredService<IPirateExploration>();
            var site = store.Signatures(system, f.Clock.Now).Single(s => s.Id == parent);
            Assert.Single(site.Objects, o => o.Id == talocan);
            new CombatAnomalyStore(new(f.Db.Path), store).Arrive(system, site.Position, f.Clock.Now);
            Assert.Equal(npcs, restarted.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).Count()));
        }
    }

    [Theory]
    [InlineData("Unsecured Frontier Digital Nexus", 4, SleeperWaveTrigger.HackStarted)]
    [InlineData("Unsecured Frontier Trinary Hub", 4, SleeperWaveTrigger.HackFailed)]
    [InlineData("Forgotten Frontier Conversion Module", 4, SleeperWaveTrigger.HackFailed)]
    [InlineData("Forgotten Core Information Pen", 5, SleeperWaveTrigger.HackResolved)]
    public async Task HackingReinforcementsWaitForTheirOwnSavedSignalAndDoNotReplay(string name, int level, SleeperWaveTrigger trigger)
    {
        using Fixture f = new();
        int system = Prepare(f, level, hacking: true);
        long analyzer = f.Fit(name.StartsWith("Unsecured", StringComparison.Ordinal) ? 22175 : 22177);
        long encounter;
        int gate;
        int count;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var store = (PirateExplorationStore)f.Db.Services.GetRequiredService<IPirateExploration>();
            var site = Choose(store, f, live, system, name);
            var containers = site.Objects.Where(o => HackingRules.IsContainer(o.Id) && !SleeperRules.IsTalocan(o.TypeId)).ToArray();
            CombatAnomalyStore sites = new(new(f.Db.Path), store);
            sites.Arrive(system, site.Position, f.Clock.Now);
            encounter = live.Economy.Inspect(world => CombatAnomalyStore.Guarded(world, site.Id)!.Id);
            using CatalogDbContext catalog = new(f.CatalogPath);
            var waves = CombatAnomalyCatalog.Find(catalog, site.DungeonId)!.Waves;
            gate = Enumerable.Range(0, waves.Length).Single(index => waves[index].Trigger == trigger);
            int steps = 0;
            while (
                live.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.Wave) < gate - 1
                || live.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter && n.DestroyedAt == null).Any())
            )
            {
                Assert.True(steps++ < 10);
                KillWave(f, live, encounter, system);
            }
            Assert.Equal(gate - 1, live.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.Wave));
            int attempts = trigger == SleeperWaveTrigger.HackResolved ? SleeperRules.HackThreshold(site.Id) : 1;
            for (int i = 0; i < attempts; i++)
            {
                var container = containers[i];
                f.Db.Store.SetPosition(f.ShipId, container.Position, system);
                var contact = store.Begin(f.PilotId, analyzer, container.Id, new HashSet<long> { container.Id }, f.Clock.Now);
                if (trigger == SleeperWaveTrigger.HackStarted)
                {
                    Assert.Equal(gate, live.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.Wave));
                    store.Cancel(f.PilotId, f.Clock.Now);
                }
                else if (trigger == SleeperWaveTrigger.HackFailed)
                {
                    Assert.Equal(gate - 1, live.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.Wave));
                    store.Cancel(f.PilotId, f.Clock.Now);
                }
                else
                {
                    Win(store, f, live);
                    Assert.Equal(
                        i + 1 < attempts ? gate - 1 : gate,
                        live.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.Wave)
                    );
                }
                store.Cancel(f.PilotId, f.Clock.Now);
                f.Clock.AdvanceWallTime(TimeSpan.FromTicks(contact.ReadyAt - f.Clock.Now));
            }
            Assert.Equal(gate, live.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.Wave));
            count = live.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).Count());
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var store = (PirateExplorationStore)f.Db.Services.GetRequiredService<IPirateExploration>();
            store.Cancel(f.PilotId, f.Clock.Now);
            Assert.Equal(gate, restarted.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.Wave));
            Assert.Equal(count, restarted.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).Count()));
        }
    }

    [Fact]
    public async Task ReceiverAggressionSpawnsItsConditionalMemberOnlyOnceInTheCorrectWave()
    {
        using Fixture f = new();
        int system = Prepare(f, 3, hacking: true);
        long ship = f.ShipId;
        long encounter;
        long target;
        int count;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var store = (PirateExplorationStore)f.Db.Services.GetRequiredService<IPirateExploration>();
            var site = Choose(store, f, live, system, "Unsecured Frontier Receiver");
            new CombatAnomalyStore(new(f.Db.Path), store).Arrive(system, site.Position, f.Clock.Now);
            encounter = live.Economy.Inspect(world => CombatAnomalyStore.Guarded(world, site.Id)!.Id);
            while (live.Economy.Inspect(world => world.Set<WorldCombatAnomaly>().Find(encounter)!.Wave) < 2)
            {
                KillWave(f, live, encounter, system);
            }
            target = live.Economy.Inspect(world =>
                world.Filter<WorldAnomalyMember>(m => m.SiteId == encounter && m.Wave == 2).First().NpcId
            );
            count = live.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).Count());
            using (var transaction = live.Economy.Begin(new(systems: [system], pilots: [f.PilotId])))
            using (CatalogDbContext catalog = new(f.CatalogPath))
            {
                var npc = transaction.Set<WorldNpc>().Find(target)!;
                NpcHostility.Record(transaction, catalog, npc, ship, f.Clock.Now);
                NpcHostility.Record(transaction, catalog, npc, ship, f.Clock.Now);
                transaction.Commit();
            }
            Assert.Equal(count + 1, live.Economy.Inspect(world => world.Filter<WorldNpc>(n => n.EncounterId == encounter).Count()));
            await live.FlushAsync();
        }
        using (LiveWorld restarted = new(new(f.Db.Path), f.Clock, _ => { }))
        using (var transaction = restarted.Economy.Begin(new(systems: [system], pilots: [f.PilotId])))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            NpcHostility.Record(transaction, catalog, transaction.Set<WorldNpc>().Find(target)!, ship, f.Clock.Now);
            Assert.Equal(count + 1, transaction.Filter<WorldNpc>(n => n.EncounterId == encounter).Count());
            transaction.Commit();
        }
    }
}
