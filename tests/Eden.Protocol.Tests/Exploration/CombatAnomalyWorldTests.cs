using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Loot;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Exploration;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Combat;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Configuration;
using Eden.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Exploration;

public sealed class CombatAnomalyWorldTests
{
    [Fact]
    public void DocumentedDefinitionsResolveCatalogGeometryAndNpcHealthForEveryVariant()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var definitions = CombatAnomalyCatalog.Read(catalog);
        Assert.Equal(199, definitions.Length);
        Assert.Equal(4, definitions.Count(d => d.Reconstructed));
        Assert.Equal(6, definitions.Select(d => d.Faction).Distinct().Count());
        NpcProfileCatalog profiles = new();
        foreach (var definition in definitions)
        {
            Assert.NotEmpty(definition.Waves);
            Assert.All(definition.Objects, body => Assert.False(body.IsGlobal));
            Assert.True(definition.Waves.Length * AnomalyEncounterRules.MembersPerWave < AnomalyEncounterRules.NpcsPerSite);
            Assert.True(definition.Objects.Length < AnomalyEncounterRules.ObjectsPerSite);
            Assert.True(definition.CorporationId > 0);
            foreach (var wave in definition.Waves)
            {
                Assert.InRange(
                    AnomalyEncounterRules.Members(CombatAnomalyRules.FirstSiteId, 0, wave).Count,
                    0,
                    AnomalyEncounterRules.MembersPerWave - 1
                );
                foreach (var group in wave.Groups)
                {
                    Assert.NotEmpty(group.Types);
                    Assert.InRange(group.Minimum, 0, group.Maximum);
                    foreach (int id in group.Types)
                    {
                        var profile = profiles.Read(catalog, id);
                        Assert.True(profile.Defense.Structure > 0, $"{definition.Name}: type {id} has no structure HP.");
                        var source = catalog.Read(CatalogData.Client.Types, (long)id)!;
                        Assert.True(source.WreckTypeID.HasValue || profile.Type.GroupId == AnomalyEncounterRules.SentryGroup);
                    }
                }
            }
        }
    }

    [Fact]
    public void StationaryBatteriesAcquireWithoutReactionDogmaAndNeverChase()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var sentries = CombatAnomalyCatalog
            .Read(catalog)
            .SelectMany(d => d.Waves)
            .SelectMany(w => w.Groups)
            .Where(g => g.Sentry)
            .SelectMany(g => g.Types)
            .Distinct();
        foreach (int id in sentries)
        {
            var profile = new NpcProfileCatalog().Read(catalog, id);
            var attributes = profile.Attributes.Where(p => p.Key != DogmaAttributeId.EntityAttackDelayMinimum).ToDictionary();
            LiveCombatNpc npc = new(
                id,
                id,
                CombatAnomalyRules.SystemId,
                SpacePosition.Zero,
                SpacePosition.Zero,
                profile.Type.Radius ?? 0,
                0,
                new(profile.Defense, profile.Defense.Undamaged, 0),
                true,
                new Dictionary<long, CombatParty>(),
                NpcRole.Pirate,
                1,
                0,
                1,
                1,
                SpacePosition.Zero,
                attributes
            )
            {
                GroupId = AnomalyEncounterRules.SentryGroup,
                AcquisitionRange = 100_000,
            };
            LiveNpcTarget target = new(42, npc.SystemId, new(50_000, 0, 0), 1);
            var aggression = Assert.Single(LiveNpcAcquisitionRules.Acquire([npc], [target], new HashSet<long>(), 1));
            Assert.Equal(target.Id, aggression.ShipId);
            var moved = LiveSystemMotionRules.Move(npc, attributes, new(42, 0, 0, 0, 0, target.Position, 1), 0, TimeSpan.TicksPerSecond, 1);
            Assert.Equal(SpacePosition.Zero, moved.Position);
            Assert.Equal(SpacePosition.Zero, moved.Velocity);
            Assert.Equal(NpcNavigationState.Idle, moved.Navigation);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatteryCanFireThroughSqlAndResidentCombatWithoutMovementInputs(bool resident)
    {
        using NpcCombatTests.Fixture fixture = new(liveWorld: resident, useTestClock: true);
        var database = new WorldDatabase(fixture.Db.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var profiles = new NpcProfileCatalog();
        var profile = CombatAnomalyCatalog
            .Read(catalog)
            .SelectMany(d => d.Waves)
            .SelectMany(w => w.Groups)
            .Where(g => g.Sentry)
            .SelectMany(g => g.Types)
            .Distinct()
            .Select(id => profiles.Read(catalog, id))
            .First(p => NpcPopulationRules.HasDirectWeapon(p.Attributes));
        const long npcId = CombatAnomalyRules.FirstNpcId;
        using (WorldDbContext world = new(database.Path))
        {
            world.Add(
                new WorldNpc
                {
                    Id = npcId,
                    TypeId = profile.Type.Id,
                    SystemId = fixture.Npc.SystemId,
                    Role = NpcRole.Pirate,
                    AnchorKind = NpcAnchorKind.Anomaly,
                    HomeX = 0,
                    HomeY = 0,
                    HomeZ = 0,
                    AcquisitionRange = 100_000,
                    UpdatedAt = fixture.Clock.Now,
                    MotionAt = fixture.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        fixture.Db.Store.SetPosition(NpcCombatTests.Fixture.ShipId, new(1000, 0, 0), fixture.Npc.SystemId);
        fixture.Combat.Acquire([new(NpcCombatTests.Fixture.PilotId, NpcCombatTests.Fixture.ShipId)], fixture.Clock.Now);
        Assert.Contains(npcId, fixture.Combat.Aggressors(NpcCombatTests.Fixture.ShipId));
        fixture.Clock.Seconds = 120;
        if (resident)
        {
            fixture.Db.Store.Advance(fixture.Npc.SystemId, 121, fixture.Clock.Now, [], []);
        }
        var shot = fixture.Combat.Fire(
            NpcCombatTests.Fixture.PilotId,
            NpcCombatTests.Fixture.ShipId,
            npcId,
            SpacePosition.Zero,
            fixture.Clock.Now,
            0.5,
            121
        );
        Assert.NotNull(shot);
        Assert.True(shot!.Applied > 0);
    }

    [Fact]
    public void AgencyDirectoryRoundTripsListsCountsAndFactionIdentity()
    {
        CombatAnomaly site = new(
            CombatAnomalyRules.FirstSiteId,
            "EDN-001",
            CombatAnomalyRules.SystemId,
            CombatAnomalyRules.DungeonId,
            1,
            1,
            SpacePosition.Zero,
            false,
            [],
            (int)NpcPirateFaction.Sansha
        );
        AnomalySystemSites[] systems = [new(site.SystemId, [site])];
        var directory = Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(new AnomalyDirectory(systems))));
        var list = Assert.IsType<List<object?>>(directory[site.SystemId]);
        var fields = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(Assert.Single(list)).State);
        Assert.Equal((long)NpcPirateFaction.Sansha, fields["factionID"]);
        var counts = Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(new AnomalyCounts(systems))));
        Assert.Equal(1L, counts[site.SystemId]);
    }

    [Fact]
    public void TelescopeAndBunkerHideawaysHaveDifferentReportedFinalWaves()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var telescope = CombatAnomalyCatalog.Find(catalog, 2134)!;
        var bunker = CombatAnomalyCatalog.Find(catalog, 2154)!;
        Assert.Contains(telescope.Objects, o => o.TypeId == CombatAnomalyRules.TelescopeTypeId);
        Assert.DoesNotContain(bunker.Objects, o => o.TypeId == CombatAnomalyRules.TelescopeTypeId);
        Assert.All(
            telescope.Waves[^1].Groups.Where(g => !g.Optional).SelectMany(g => g.Types),
            id => Assert.Contains(id, new[] { CombatAnomalyRules.PithiInfiltrator, CombatAnomalyRules.PithiInvader })
        );
        Assert.All(
            bunker.Waves[^1].Groups.Where(g => !g.Optional).SelectMany(g => g.Types),
            id => Assert.Contains(catalog.RequireItem(id).Name, new[] { "Pithi Wrecker", "Pithi Plunderer" })
        );
    }

    [Fact]
    public void GenericCommandersPreserveHullClassAndDroneNamesPreferDeadspaceTypes()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var definitions = CombatAnomalyCatalog.Read(catalog);
        var hideaway = definitions.First(d => d.Name == "Serpentis Forlorn Hideaway");
        var commanders = hideaway.Waves.SelectMany(w => w.Groups).Where(g => g.Optional).SelectMany(g => g.Types).Distinct().ToArray();
        Assert.NotEmpty(commanders);
        const int serpentisCommanderFrigateGroup = 814; // Pinned client/groups, Asteroid Serpentis Commander Frigate.
        Assert.All(commanders, id => Assert.Equal(serpentisCommanderFrigateGroup, catalog.RequireItem(id).GroupId));
        var drones = definitions
            .Where(d => d.Faction == NpcPirateFaction.RogueDrones)
            .SelectMany(d => d.Waves)
            .SelectMany(w => w.Groups)
            .Where(g => !g.Sentry)
            .SelectMany(g => g.Types)
            .Distinct();
        var allowed = NpcPirateGroups
            .EncounterLoot.Where(g => g.Faction == NpcPirateFaction.RogueDrones)
            .Select(g => g.GroupId)
            .ToHashSet();
        const int rogueDroneCarrierGroup = 1691; // Pinned carrier group, reported optional Horde spawn.
        allowed.Add(rogueDroneCarrierGroup);
        Assert.All(drones, id => Assert.Contains(catalog.RequireItem(id).GroupId, allowed));
    }

    [Fact]
    public void Schema109UpgradePreservesActiveLegacyEncounterAndNpcRows()
    {
        using NpcCombatTests.Fixture fixture = new();
        var database = new WorldDatabase(fixture.Db.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using (WorldDbContext world = new(database.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldCombatAnomaly;
                DROP TABLE WorldAnomalyMember;
                CREATE TABLE WorldCombatAnomaly (
                    Id INTEGER PRIMARY KEY, SystemId INTEGER NOT NULL, Wave INTEGER NOT NULL,
                    X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, CompletedAt INTEGER, ReplaceAt INTEGER);
                INSERT INTO WorldCombatAnomaly VALUES (7000000000000,30000144,1,1e12,1e12,1e12,NULL,NULL);
                ALTER TABLE WorldNpc DROP COLUMN AcquisitionRange;
                PRAGMA user_version=109;
                """
            );
        }
        WorldInitialization.Initialize(database, fixture.Clock);
        WorldInitialization.Initialize(database, fixture.Clock);
        using WorldDbContext saved = new(database.Path);
        var site = Assert.Single(saved.Set<WorldCombatAnomaly>());
        Assert.Equal(CombatAnomalyRules.DungeonId, site.DungeonId);
        Assert.Equal(1, site.Wave);
        Assert.Equal(0, site.DefinitionVersion);
        Assert.Null(saved.Set<WorldNpc>().Find(fixture.Npc.Id)!.AcquisitionRange);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void CommanderRewardsAreFactionSpecificAndDeterministicAcrossRetries()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        foreach (var faction in Enum.GetValues<NpcPirateFaction>())
        {
            var common = NpcCommanderLootRules.Roll(faction, 1);
            var rare = NpcCommanderLootRules.Roll(faction, 0);
            Assert.Equal(2, common.Count);
            Assert.Equal(3, rare.Count);
            Assert.Equal(rare, NpcCommanderLootRules.Roll(faction, 0));
            Assert.All(rare, drop => Assert.True(catalog.RequireItem(drop.TypeId).Id > 0));
            Assert.Contains(NpcPirateGroups.EncounterLoot, group => group.Faction == faction && group.Commander);
        }
        Assert.Equal(30, NpcPirateGroups.All.Count);
        Assert.Empty(NpcPirateGroups.All.Select(g => g.GroupId).Intersect(NpcPirateGroups.EncounterLoot.Select(g => g.GroupId)));
    }

    [Fact]
    public void RegionalSystemsPublishMultipleIndependentSitesAndWarmReadsAvoidSql()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        CombatAnomalyStore sites = new(database);
        const long now = 134000000000000000;
        var perimeter = sites.List(CombatAnomalyRules.SystemId, now);
        Assert.Equal(AnomalyEncounterRules.SitesPerSystem, perimeter.Count);
        Assert.All(
            perimeter,
            s => Assert.Contains(s.FactionId, new[] { (int)NpcPirateFaction.Guristas, (int)NpcPirateFaction.RogueDrones })
        );
        Assert.Equal(perimeter.Count, perimeter.Select(s => s.Id).Distinct().Count());
        Assert.Equal(perimeter.Count, perimeter.Select(s => s.Position).Distinct().Count());
        var other = sites.List(30000142, now);
        Assert.Equal(AnomalyEncounterRules.SitesPerSystem, other.Count);
        Assert.Empty(perimeter.Select(s => s.Id).Intersect(other.Select(s => s.Id)));
        var directory = sites.Directory(now);
        Assert.Equal(2, directory.Count);
        using var metrics = SessionPerformance.Begin("anomaly-system-reads", _ => { }, force: true)!;
        Assert.Same(perimeter, sites.List(CombatAnomalyRules.SystemId, now + 1));
        Assert.Same(other, sites.List(30000142, now + 1));
        Assert.Same(directory, sites.Directory(now + 1));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void PopulationPoliciesSurviveRestartAndPreserveExistingSites()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        var settings = new WorldConfigurationStore(database, TimeProvider.System);
        var state = settings.Read();
        Assert.False(state.Active.Draft.Settings.NpcPopulation.MajorThreatAnomalies);
        CombatAnomalyStore sites = new(database);
        const long now = 134000000000000000;
        var previous = sites.List(CombatAnomalyRules.SystemId, now);
        settings.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with
                {
                    NpcPopulation = state.Active.Draft.Settings.NpcPopulation with { Pirates = false, MajorThreatAnomalies = true },
                },
            },
            "test"
        );
        WorldInitialization.Initialize(database);
        Assert.True(database.Settings.NpcPopulation.MajorThreatAnomalies);
        Assert.False(database.Settings.NpcPopulation.Pirates);
        Assert.Equal(previous.Select(s => s.Id), sites.List(CombatAnomalyRules.SystemId, now).Select(s => s.Id));
        Assert.Empty(sites.List(30000142, now));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var advanced = CombatAnomalyCatalog.Read(catalog).Where(d => d.RequiresMajorThreat).ToArray();
        Assert.Equal(6, advanced.Length);
        Assert.All(
            advanced,
            d =>
            {
                Assert.False(AnomalyEncounterRules.Allows(d.Name, 0.7));
                Assert.False(AnomalyEncounterRules.Allows(d.Name, 0.3));
                Assert.True(AnomalyEncounterRules.Allows(d.Name, -0.3));
            }
        );
    }

    [Theory]
    [InlineData("Guristas Hideaway", 0.7, true)]
    [InlineData("Guristas Hideaway", -0.2, false)]
    [InlineData("Guristas Hidden Den", 0.7, false)]
    [InlineData("Guristas Hidden Den", 0.3, true)]
    [InlineData("Guristas Hidden Den", -0.2, true)]
    [InlineData("Angel Yard", 0.3, true)]
    [InlineData("Angel Yard", -0.2, false)]
    [InlineData("Serpentis Haven", 0.3, false)]
    [InlineData("Serpentis Haven", -0.5, true)]
    [InlineData("Drone Horde", -0.5, true)]
    [InlineData("Drone Collection", 0.7, true)]
    public void SecurityBandsUseTheDocumentedSiteFamilies(string name, double security, bool expected)
    {
        Assert.Equal(expected, AnomalyEncounterRules.Allows(name, security));
    }

    private static WorldCombatAnomaly Seed(WorldDatabase database, CatalogDbContext catalog, string name)
    {
        var definition = CombatAnomalyCatalog.Read(catalog).First(d => d.Name == name);
        WorldCombatAnomaly site = new()
        {
            Id = CombatAnomalyRules.FirstSiteId,
            SystemId = CombatAnomalyRules.SystemId,
            DungeonId = definition.DungeonId,
            DefinitionVersion = 1,
            X = 1e12,
            Y = 1e12,
            Z = 1e12,
        };
        using WorldDbContext world = new(database.Path);
        world.Add(site);
        world.SaveChanges();
        return site;
    }

    [Fact]
    public void TriggerGroupsReleaseReinforcementsWhileEarlierHostilesSurvive()
    {
        using NpcCombatTests.Fixture fixture = new();
        var database = new WorldDatabase(fixture.Db.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var site = Seed(database, catalog, "Blood Hidden Den");
        CombatAnomalyStore sites = new(database);
        sites.Arrive(site.SystemId, new(site.X, site.Y, site.Z), fixture.Clock.Now);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var triggers = world.Set<WorldAnomalyMember>().Where(m => m.SiteId == site.Id && m.Wave == 0 && m.Trigger).ToArray();
        Assert.NotEmpty(triggers);
        foreach (var member in triggers)
        {
            var npc = world.Set<WorldNpc>().Find(member.NpcId)!;
            npc.DestroyedAt = fixture.Clock.Now;
            CombatAnomalyStore.Killed(world, catalog, npc, fixture.Clock.Now);
        }
        world.SaveChanges();
        transaction.Commit();
        Assert.Equal(1, world.Set<WorldCombatAnomaly>().Find(site.Id)!.Wave);
        Assert.True(
            world
                .Set<WorldNpc>()
                .Any(n =>
                    n.EncounterId == site.Id
                    && n.DestroyedAt == null
                    && world.Set<WorldAnomalyMember>().Any(m => m.NpcId == n.Id && m.Wave == 0)
                )
        );
        Assert.True(world.Set<WorldAnomalyMember>().Any(m => m.SiteId == site.Id && m.Wave == 1));
    }

    [Fact]
    public void TimedReinforcementsHonorSavedDeadlinesAcrossReopen()
    {
        using NpcCombatTests.Fixture fixture = new();
        var database = new WorldDatabase(fixture.Db.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var site = Seed(database, catalog, "Blood Den");
        CombatAnomalyStore sites = new(database);
        sites.Arrive(site.SystemId, new(site.X, site.Y, site.Z), fixture.Clock.Now);
        long deadline;
        using (WorldDbContext world = new(database.Path))
        {
            deadline = world.Set<WorldCombatAnomaly>().Find(site.Id)!.NextWaveAt!.Value;
        }
        WorldInitialization.Initialize(database, fixture.Clock);
        sites = new(database);
        _ = sites.List(site.SystemId, deadline - 1);
        using (WorldDbContext world = new(database.Path))
        {
            Assert.Equal(0, world.Set<WorldCombatAnomaly>().Find(site.Id)!.Wave);
        }
        _ = sites.List(site.SystemId, deadline);
        _ = sites.List(site.SystemId, deadline);
        using WorldDbContext saved = new(database.Path);
        Assert.Equal(1, saved.Set<WorldCombatAnomaly>().Find(site.Id)!.Wave);
        Assert.True(saved.Set<WorldNpc>().Any(n => n.EncounterId == site.Id && n.DestroyedAt == null));
    }

    [Fact]
    public void FullEncounterSettlesBountiesWrecksAndIndependentReplacement()
    {
        using NpcCombatTests.Fixture fixture = new();
        var database = new WorldDatabase(fixture.Db.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var site = Seed(database, catalog, "Guristas Burrow");
        CombatAnomalyStore sites = new(database);
        sites.Arrive(site.SystemId, new(site.X, site.Y, site.Z), fixture.Clock.Now);
        int killed = 0;
        for (int cycle = 0; cycle < 30; cycle++)
        {
            using WorldDbContext world = new(database.Path);
            var active = world.Set<WorldNpc>().Where(n => n.EncounterId == site.Id && n.DestroyedAt == null).ToArray();
            if (active.Length == 0)
            {
                break;
            }
            using var transaction = world.Database.BeginTransaction();
            foreach (var npc in active)
            {
                var profile = new NpcProfileCatalog().Read(catalog, npc.TypeId);
                var result = NpcDamagePersistence.Apply(
                    world,
                    catalog,
                    npc,
                    world.Pilots.Find(NpcCombatTests.Fixture.PilotId)!,
                    NpcCombatTests.Fixture.ShipId,
                    profile.Attributes,
                    new DamageVector(1e9, 1e9, 1e9, 1e9),
                    fixture.Clock.Now
                );
                Assert.True(result.Destroyed);
                killed++;
            }
            world.SaveChanges();
            transaction.Commit();
        }
        using (WorldDbContext saved = new(database.Path))
        {
            Assert.NotNull(saved.Set<WorldCombatAnomaly>().Find(site.Id)!.CompletedAt);
            Assert.Equal(killed, saved.Set<WorldWreck>().Count(w => w.NpcId >= CombatAnomalyRules.FirstNpcId));
            Assert.Equal(killed, saved.Set<NpcBounty>().Count());
        }
        Assert.DoesNotContain(sites.List(site.SystemId, fixture.Clock.Now), s => s.Id == site.Id);
        Assert.Equal(AnomalyEncounterRules.SitesPerSystem - 1, sites.List(site.SystemId, fixture.Clock.Now).Count);
    }
}
