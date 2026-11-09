using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Exploration;

public sealed class CombatAnomalyLayoutTests
{
    private const long Now = 134000000000000000;
    private static readonly SpacePosition Origin = new(1e12, 1e12, 1e12);

    private sealed record SavedPlacement(long Id, double X, double Y, double Z, double? AcquisitionRange);

    [Fact]
    public void DroneHordeSentriesUseTheirIndividualCatalogPositions()
    {
        using TestWorld test = new();
        var database = new WorldDatabase(test.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        const int ringHordeDungeon = 2352; // Pinned Drone Horde layout with eight Tower Sentry Drone III objects.
        Seed(database, ringHordeDungeon);
        var dungeon = catalog.Read(CatalogData.Client.Dungeons, ringHordeDungeon)!;
        var room = dungeon.Rooms!.Single().Value;
        var entry = room.Objects!.Values.Single(o => o.ObjectID == dungeon.EntryObjectID).Position!;
        var expected = room
            .Objects.Values.Where(o => catalog.RequireItem(checked((int)o.TypeID!.Value)).GroupId == AnomalyEncounterRules.SentryGroup)
            .Select(o =>
                Origin
                + new SpacePosition(
                    o.Position!.X!.Value - entry.X!.Value,
                    o.Position.Y!.Value - entry.Y!.Value,
                    o.Position.Z!.Value - entry.Z!.Value
                )
            )
            .ToHashSet();

        new CombatAnomalyStore(database).Arrive(CombatAnomalyRules.SystemId, Origin, Now);
        using WorldDbContext saved = new(database.Path);
        var sentries = saved
            .Set<WorldNpc>()
            .Where(n => n.EncounterId == CombatAnomalyRules.FirstSiteId)
            .ToArray()
            .Where(n => catalog.RequireItem(n.TypeId).GroupId == AnomalyEncounterRules.SentryGroup)
            .ToArray();
        Assert.Equal(8, expected.Count);
        Assert.Equal(expected.Count, sentries.Length);
        Assert.All(sentries, n => Assert.Contains(new SpacePosition(n.HomeX!.Value, n.HomeY!.Value, n.HomeZ!.Value), expected));
        Assert.Equal(
            expected.Count,
            sentries.Select(n => new SpacePosition(n.HomeX!.Value, n.HomeY!.Value, n.HomeZ!.Value)).Distinct().Count()
        );
    }

    [Theory]
    [InlineData(2146)]
    [InlineData(2172)]
    [InlineData(2113)]
    [InlineData(2159)]
    [InlineData(2280)]
    [InlineData(2282)]
    [InlineData(2309)]
    [InlineData(2201)]
    [InlineData(2221)]
    [InlineData(2356)]
    [InlineData(2312)]
    [InlineData(2322)]
    [InlineData(2290)]
    [InlineData(2310)]
    [InlineData(5639)]
    [InlineData(2204)]
    [InlineData(2300)]
    [InlineData(2308)]
    [InlineData(2276)]
    public void PhysicalVariantMatchesItsNativeBatteryTypesAndCount(int dungeonId)
    {
        using TestWorld test = new();
        var database = new WorldDatabase(test.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var definition = CombatAnomalyCatalog.Find(catalog, dungeonId)!;
        var dungeon = catalog.Read(CatalogData.Client.Dungeons, dungeonId)!;
        var native = dungeon
            .Rooms!.Values.SelectMany(r => r.Objects!.Values)
            .Where(o => catalog.RequireItem(checked((int)o.TypeID!.Value)).GroupId == AnomalyEncounterRules.SentryGroup)
            .ToArray();
        var reported = definition.Waves.SelectMany(w => w.Groups).Where(g => g.Sentry).ToArray();
        Assert.Equal(
            native.Select(o => checked((int)o.TypeID!.Value)).Distinct().Order(),
            reported.SelectMany(g => g.Types).Distinct().Order()
        );
        Assert.InRange(native.Length, reported.Sum(g => g.Minimum), reported.Sum(g => g.Maximum));
    }

    [Fact]
    public void ReinforcementPlacementRollsBackAndPersistsAcrossReopen()
    {
        using TestWorld test = new();
        var database = new WorldDatabase(test.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        const int forlornHubDungeon = 3454; // Native initial anchor and three separate reinforcement anchors.
        Seed(database, forlornHubDungeon);
        CombatAnomalyStore sites = new(database);
        sites.Arrive(CombatAnomalyRules.SystemId, Origin, Now);
        var initial = Snapshot(database);
        using (WorldDbContext world = new(database.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            DestroyInitialWave(world, catalog);
            Assert.Equal(1, world.Set<WorldCombatAnomaly>().Find(CombatAnomalyRules.FirstSiteId)!.Wave);
            transaction.Rollback();
        }
        Assert.Equal(initial, Snapshot(database));
        using (WorldDbContext world = new(database.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            DestroyInitialWave(world, catalog);
            transaction.Commit();
        }
        var reinforcement = Snapshot(database);
        Assert.NotEmpty(reinforcement);
        var definition = CombatAnomalyCatalog.Find(catalog, forlornHubDungeon)!;
        var anchors = definition
            .SpawnAnchors.Where(a => a.Reinforcement && a.SentryTypeId is null)
            .Select(a => Origin + a.Position)
            .ToHashSet();
        Assert.Contains(reinforcement, n => anchors.Contains(new(n.X, n.Y, n.Z)));

        new CombatAnomalyStore(new(database.Path)).Arrive(CombatAnomalyRules.SystemId, Origin, Now);
        Assert.Equal(reinforcement, Snapshot(database));
    }

    [Fact]
    public void EveryExistingLayoutHasFiniteCatalogAnchorsAndDeterministicPlacements()
    {
        using TestWorld test = new();
        var database = new WorldDatabase(test.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var definitions = CombatAnomalyCatalog.Read(catalog);
        Assert.Equal(199, definitions.Length);
        foreach (var definition in definitions)
        {
            Assert.NotEmpty(definition.SpawnAnchors);
            for (int wave = 0; wave < definition.Waves.Length; wave++)
            {
                var members = AnomalyEncounterRules.Members(CombatAnomalyRules.FirstSiteId, wave, definition.Waves[wave]);
                var fallback = new AnomalySpawnPlacement(definition.Spawner, definition.AcquisitionRange);
                var placements = AnomalySpawnRules.Place(wave, members, definition.SpawnAnchors, fallback);
                Assert.Equal(members.Count, placements.Length);
                Assert.Equal(placements.ToArray(), AnomalySpawnRules.Place(wave, members, definition.SpawnAnchors, fallback).ToArray());
                Assert.All(
                    placements,
                    p =>
                    {
                        Assert.True(double.IsFinite(p.Position.X) && double.IsFinite(p.Position.Y) && double.IsFinite(p.Position.Z));
                        Assert.True(double.IsFinite(p.AcquisitionRange) && p.AcquisitionRange > 0);
                    }
                );
            }
        }
    }

    [Fact]
    public void StartedVersionOneSiteKeepsItsRecipeAndSavedReinforcementDeadline()
    {
        using TestWorld test = new();
        var database = new WorldDatabase(test.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        const int dirtyShipyardHub = 2146; // Previously bound to the Small Armory recipe.
        long deadline = Now + 10 * TimeSpan.TicksPerSecond;
        Seed(database, dirtyShipyardHub);
        using (WorldDbContext world = new(database.Path))
        {
            var site = world.Set<WorldCombatAnomaly>().Find(CombatAnomalyRules.FirstSiteId)!;
            site.Wave = 0;
            site.NextWaveAt = deadline;
            world.SaveChanges();
        }
        var definition = CombatAnomalyCatalog.Find(catalog, dirtyShipyardHub)!;
        var expected = AnomalyEncounterRules.Members(CombatAnomalyRules.FirstSiteId, 1, definition.WavesFor(1)[1]);
        var corrected = AnomalyEncounterRules.Members(CombatAnomalyRules.FirstSiteId, 1, definition.Waves[1]);
        Assert.NotEqual(expected.Count, corrected.Count);

        new CombatAnomalyStore(new(database.Path)).List(CombatAnomalyRules.SystemId, deadline - 1);
        using (WorldDbContext world = new(database.Path))
        {
            Assert.Equal(0, world.Set<WorldCombatAnomaly>().Find(CombatAnomalyRules.FirstSiteId)!.Wave);
        }
        new CombatAnomalyStore(new(database.Path)).List(CombatAnomalyRules.SystemId, deadline);
        using (WorldDbContext world = new(database.Path))
        {
            var site = world.Set<WorldCombatAnomaly>().Find(CombatAnomalyRules.FirstSiteId)!;
            Assert.Equal(1, site.DefinitionVersion);
            Assert.Equal(1, site.Wave);
            Assert.Equal(
                expected.Select(m => m.TypeId),
                world.Set<WorldNpc>().Where(n => n.EncounterId == site.Id).OrderBy(n => n.Id).Select(n => n.TypeId).ToArray()
            );
        }
    }

    [Fact]
    public void UnvisitedVersionOneSiteAdoptsCorrectedRecipeOnArrival()
    {
        using TestWorld test = new();
        var database = new WorldDatabase(test.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        const int dirtyShipyardHub = 2146;
        Seed(database, dirtyShipyardHub);
        new CombatAnomalyStore(database).Arrive(CombatAnomalyRules.SystemId, Origin, Now);
        using WorldDbContext world = new(database.Path);
        var site = world.Set<WorldCombatAnomaly>().Find(CombatAnomalyRules.FirstSiteId)!;
        Assert.Equal(AnomalyEncounterRules.CurrentDefinitionVersion, site.DefinitionVersion);
        var definition = CombatAnomalyCatalog.Find(catalog, dirtyShipyardHub)!;
        var expected = AnomalyEncounterRules.Members(site.Id, 0, definition.Waves[0]);
        Assert.Equal(
            expected.Select(m => m.TypeId),
            world.Set<WorldNpc>().Where(n => n.EncounterId == site.Id).OrderBy(n => n.Id).Select(n => n.TypeId).ToArray()
        );
    }

    private static void DestroyInitialWave(WorldDbContext world, CatalogDbContext catalog)
    {
        var initial = world.Set<WorldNpc>().Where(n => n.EncounterId == CombatAnomalyRules.FirstSiteId && n.DestroyedAt == null).ToArray();
        foreach (var npc in initial)
        {
            npc.DestroyedAt = Now;
            CombatAnomalyStore.Killed(world, catalog, npc, Now);
        }
        world.SaveChanges();
    }

    private static SavedPlacement[] Snapshot(WorldDatabase database)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<WorldNpc>()
            .Where(n => n.EncounterId == CombatAnomalyRules.FirstSiteId && n.DestroyedAt == null)
            .OrderBy(n => n.Id)
            .Select(n => new SavedPlacement(n.Id, n.X, n.Y, n.Z, n.AcquisitionRange))
            .ToArray();
    }

    private static void Seed(WorldDatabase database, int dungeonId)
    {
        using WorldDbContext world = new(database.Path);
        world.Add(
            new WorldCombatAnomaly
            {
                Id = CombatAnomalyRules.FirstSiteId,
                SystemId = CombatAnomalyRules.SystemId,
                DungeonId = dungeonId,
                DefinitionVersion = 1,
                X = Origin.X,
                Y = Origin.Y,
                Z = Origin.Z,
            }
        );
        world.SaveChanges();
    }
}
