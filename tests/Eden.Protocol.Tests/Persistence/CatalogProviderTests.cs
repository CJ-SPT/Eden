using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CatalogProviderTests
{
    [Fact]
    public void BulkLookupsShareInstancesAndCacheAbsentRecordsWithoutLoadingUnrequestedPayloads()
    {
        using TestWorld world = new();
        var catalog = CatalogProvider.Open(new WorldDatabase(world.Path).CatalogPath);
        long[] keys = [601, 3638, long.MaxValue, 601];
        var original = catalog.FindMany(CatalogData.Client.Types, keys);
        Assert.Equal(2, original.Count);
        Assert.Same(original[601], catalog.Get(CatalogData.Client.Types, 601L));
        using var metrics = SessionPerformance.Begin("warm-bulk-catalog", _ => { }, force: true)!;
        var repeated = catalog.FindMany(CatalogData.Client.Types, keys);
        Assert.Same(original[601], repeated[601]);
        Assert.Same(original[3638], repeated[3638]);
        Assert.False(repeated.ContainsKey(long.MaxValue));
        Assert.Null(catalog.Find(CatalogData.Client.Types, long.MaxValue));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
    }

    [Fact]
    public void WarmTravelIndexesMatchTheCatalogAndKeepMissingValues()
    {
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        using CatalogDbContext catalog = new(path);
        var definitions = catalog.Definitions;
        var gate = definitions.Gates.Values.First();
        var body = Assert.IsType<CatalogCelestialDefinition>(catalog.FindCelestial(gate.Id));
        var system = catalog.Read(CatalogData.Client.Systems, (long)gate.SolarSystemId);
        var systemContent = catalog.Read(CatalogData.Client.Solarsystemcontent, (long)gate.SolarSystemId);
        var belts = catalog.CelestialsInSystem(gate.SolarSystemId).Where(body => body.GroupId == AsteroidDefinitions.BeltGroup).ToArray();
        Assert.Equal(body, catalog.FindGateBody(gate.Id));
        Assert.Equal(new CatalogSystemPolitics(system?.SecurityStatus, system?.FactionID), catalog.SystemPolitics(gate.SolarSystemId));
        Assert.Equal(systemContent?.Security, catalog.SystemContentSecurity(gate.SolarSystemId));
        Assert.Equal(belts, catalog.BeltAnchors(gate.SolarSystemId));

        using var metrics = SessionPerformance.Begin("warm catalog travel indexes", _ => { }, force: true)!;
        Assert.Equal(body, catalog.FindGateBody(gate.Id));
        Assert.Null(catalog.FindGateBody(long.MaxValue));
        Assert.Null(catalog.FindGateBody(0));
        Assert.Equal(default, catalog.SystemPolitics(0));
        Assert.Null(catalog.SystemContentSecurity(0));
        Assert.Empty(catalog.BeltAnchors(0));
        Assert.Equal(belts, catalog.BeltAnchors(gate.SolarSystemId));
        Assert.Equal(systemContent?.Security, catalog.SystemContentSecurity(gate.SolarSystemId));
        _ = catalog.SystemPolitics(gate.SolarSystemId);
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void MercoxitDestinationSystemsMatchBeltRowsWithOneCatalogRead()
    {
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        using CatalogDbContext catalog = new(path);
        var belts = catalog
            .Records(CatalogData.Client.Asteroidcountbytypeidbeltid)
            .Where(entry =>
                entry.Key is > 0 and <= int.MaxValue
                && entry.Value.Any(ore =>
                    ore.Value > 0
                    && int.TryParse(ore.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int type)
                    && catalog.FittingType(type)?.GroupId == MiningDefinitions.MercoxitGroup
                )
            )
            .Select(entry => (int)entry.Key)
            .ToHashSet();
        var expected = catalog
            .Set<Celestial>()
            .Where(belt => belt.GroupId == AsteroidDefinitions.BeltGroup)
            .Select(belt => new { belt.Id, belt.SolarSystemId })
            .AsEnumerable()
            .Where(belt => belts.Contains(belt.Id))
            .Select(belt => belt.SolarSystemId)
            .Distinct()
            .Order()
            .ToArray();

        using var metrics = SessionPerformance.Begin("Mercoxit destination catalog read", _ => { }, force: true)!;
        using CatalogDbContext projected = new(path);
        Assert.Equal(expected, projected.BeltSystemsWithOreGroup(MiningDefinitions.MercoxitGroup).Order());
        Assert.Equal(1, metrics.Commands);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void HostRetainsTheLatestCatalogGenerationAcrossGarbageCollection()
    {
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        var first = CaptureWeak(path);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.True(first.TryGetTarget(out var original));
        long originalGeneration = original.Identity.Generation;
        CatalogProvider.Invalidate(path);
        var second = CaptureWeak(path);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.True(second.TryGetTarget(out var latest));
        Assert.NotEqual(originalGeneration, latest.Identity.Generation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<CatalogProvider> CaptureWeak(string path) => new(CatalogProvider.Open(path));

    [Fact]
    public async Task ConcurrentReadersShareDefinitionsAndWarmReadsPerformNoSql()
    {
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        var catalog = CatalogProvider.Open(path);
        Assert.Same(catalog, CatalogProvider.Open(path));
        ClientTypes[] values;
        using (var cold = SessionPerformance.Begin("concurrent-catalog", _ => { }, force: true)!)
        {
            values = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => catalog.Get(CatalogData.Client.Types, 601L))));
            Assert.Equal(1, cold.Commands);
            Assert.Equal(1, cold.Connections);
            Assert.Equal(0, cold.FittingCalculations);
        }
        Assert.All(values, value => Assert.Same(values[0], value));
        Assert.Equal(1, catalog.Statistics.Loads);
        Assert.False(catalog.TryGet(CatalogData.Client.Types, long.MaxValue, out _));
        var attributes = catalog.Attributes(601);
        var effects = catalog.Effects(3651);
        var requirements = catalog.RequiredSkills(3638);
        Assert.Contains(DogmaEffect.MiningLaser, effects);
        using var metrics = SessionPerformance.Begin("warm-catalog", _ => { }, force: true)!;
        for (int iteration = 0; iteration < 10; iteration++)
        {
            Assert.Same(values[0], catalog.Get(CatalogData.Client.Types, 601L));
            Assert.False(catalog.TryGet(CatalogData.Client.Types, long.MaxValue, out _));
            Assert.Same(attributes, catalog.Attributes(601));
            Assert.Equal(effects, catalog.Effects(3651));
            Assert.Same(requirements, catalog.RequiredSkills(3638));
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void WritableCatalogInvalidatesFutureOperationsWithoutChangingCapturedDefinitions()
    {
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        CatalogReadScope scope = new();
        CatalogProvider original;
        ClientTypes oldType;
        using (scope.Begin())
        {
            original = CatalogProvider.Open(path);
            oldType = original.Get(CatalogData.Client.Types, 601L);
            using (CatalogDbContext writable = new(path, writable: true))
            {
                writable.Database.ExecuteSqlRaw(
                    "UPDATE SourceRecord SET Data={0} WHERE Dataset='client/types' AND Key='601'",
                    "{\"volume\":123}"
                );
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            CatalogProvider? warmed = null;
            for (int read = 0; read < 128; read++)
            {
                warmed = CatalogProvider.Open(path);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Same(original, warmed);
            Assert.Equal(0, allocated);
            Assert.Same(oldType, original.Get(CatalogData.Client.Types, 601L));
            Assert.Throws<InvalidOperationException>(() => original.Get(CatalogData.Client.Types, 3651L));
        }
        var changed = CatalogProvider.Open(path);
        Assert.NotSame(original, changed);
        Assert.NotEqual(original.Identity.Generation, changed.Identity.Generation);
        Assert.Equal(15000, oldType.Volume);
        Assert.Equal(123, changed.Get(CatalogData.Client.Types, 601L).Volume);
    }

    [Fact]
    public void SeparateCatalogFilesAndMissingResultsRemainIsolated()
    {
        using TestWorld firstWorld = new();
        using TestWorld secondWorld = new();
        string firstPath = new WorldDatabase(firstWorld.Path).CatalogPath;
        string secondPath = new WorldDatabase(secondWorld.Path).CatalogPath;
        using (CatalogDbContext writable = new(secondPath, writable: true))
        {
            writable.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data={0} WHERE Dataset='client/types' AND Key='601'",
                "{\"volume\":8}"
            );
        }
        var first = CatalogProvider.Open(firstPath);
        var second = CatalogProvider.Open(secondPath);
        Assert.NotEqual(first.Identity.Path, second.Identity.Path);
        Assert.Equal(15000, first.Get(CatalogData.Client.Types, 601L).Volume);
        Assert.Equal(8, second.Get(CatalogData.Client.Types, 601L).Volume);
        Assert.Empty(first.Attributes(int.MaxValue));
        using var metrics = SessionPerformance.Begin("missing-catalog", _ => { }, force: true)!;
        Assert.Empty(first.Attributes(int.MaxValue));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
    }

    [Fact]
    public void PreloadBuildsSharedItemAndProductIndexesWithoutEnablingContent()
    {
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        using (CatalogDbContext writable = new(path, writable: true))
        {
            writable.Add(
                new SourceRecord
                {
                    Dataset = "client/blueprints",
                    Key = JsonSerializer.Serialize("785"),
                    Data = "{\"blueprintTypeID\":785,\"activities\":{\"manufacturing\":{\"products\":[{\"typeID\":48,\"quantity\":1}]}}}",
                }
            );
            writable.SaveChanges();
        }
        var catalog = CatalogProvider.Open(path);
        catalog.PreloadCore();
        Assert.Equal(6, catalog.Items[601].CategoryId);
        Assert.Equal(10000002, catalog.Systems[30000142].RegionId);
        Assert.Equal(30000142, catalog.Stations[60003760].SolarSystemId);
        Assert.Null(catalog.FindStation(long.MinValue + 60003760));
        Assert.Contains(601, catalog.TypesInGroup(catalog.Items[601].GroupId));
        Assert.Equal<int>([785], catalog.BlueprintsForProduct(48));
        Assert.Empty(catalog.BlueprintsForProduct(int.MaxValue));
        using var metrics = SessionPerformance.Begin("pinned-catalog", _ => { }, force: true)!;
        catalog.PreloadCore();
        Assert.Equal(15000, catalog.Get(CatalogData.Client.Types, 601L).Volume);
        Assert.False(catalog.TryGet(CatalogData.Client.Types, long.MaxValue, out _));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
    }

    [Fact]
    public void PreparedCatalogServesColdProjectedAndSourceLookupsWithGameplaySqlGuarded()
    {
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        using CatalogDbContext catalog = new(path);
        var provider = catalog.Definitions;
        var gate = provider.Gates.Values.First();
        var expectedOrigin = catalog.FirstCelestialPositions()[gate.SolarSystemId];
        var expectedAttributes = provider.Attributes(601);
        var expectedOreSystems = catalog.BeltSystemsWithOreGroup(MiningDefinitions.MercoxitGroup).Order().ToArray();
        provider.PrepareRuntime();

        using var sql = GameplaySqlGuard.Begin();
        using var metrics = SessionPerformance.Begin("prepared catalog", _ => { }, force: true)!;
        Assert.Equal(expectedOrigin, catalog.FirstCelestialPositions()[gate.SolarSystemId]);
        Assert.Equal(
            expectedAttributes.OrderBy(pair => pair.Key),
            provider.AttributeSets([601, int.MaxValue])[601].OrderBy(pair => pair.Key)
        );
        Assert.Equal(expectedOreSystems, catalog.BeltSystemsWithOreGroup(MiningDefinitions.MercoxitGroup).Order());
        Assert.Equal(gate.SolarSystemId, catalog.StaticGateSystems()[gate.Id]);
        Assert.Contains(provider.FindCelestial(gate.Id), provider.CelestialsInSystem(gate.SolarSystemId));
        Assert.Null(catalog.Read(CatalogData.Client.Skillplans, long.MaxValue));
        Assert.Null(catalog.Read(CatalogData.Client.Skins, "missing"));
        Assert.Null(catalog.Read(CatalogData.Client.Skinlicenses, "missing"));
        Assert.Empty(catalog.ReadMany(CatalogData.Client.LocalizationFsdEnUs, [long.MaxValue]));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
    }

    [Fact]
    public void GameplayRejectsDirectCatalogQueriesBeforeOpeningSqlite()
    {
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        using CatalogDbContext catalog = new(path);
        using var metrics = SessionPerformance.Begin("gameplay guard", _ => { }, force: true)!;
        using (GameplaySqlGuard.Begin())
        {
            var error = Assert.Throws<InvalidOperationException>(() => catalog.Set<SourceRecord>().Any());
            Assert.Contains("catalog context", error.Message);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
    }

    [Fact]
    public void PagingUsesStableSourceKeysAndLazyCacheStaysWithinItsBudget()
    {
        using TestWorld world = new();
        var options = new CatalogCacheOptions { MaximumEstimatedBytes = 1024 };
        var catalog = CatalogProvider.Open(new WorldDatabase(world.Path).CatalogPath, options);
        Assert.Same(catalog, CatalogProvider.Open(catalog.Identity.Path));
        List<long> keys = [];
        string? cursor = null;
        do
        {
            var page = catalog.Page(CatalogData.Client.Types, cursor, 1);
            keys.AddRange(page.Rows.Select(row => row.Key));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Contains(601, keys);
        Assert.Equal(keys.OrderBy(key => key.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal), keys);
        Assert.InRange(catalog.Statistics.EstimatedBytes, 0, options.MaximumEstimatedBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Page(CatalogData.Client.Types, limit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            catalog.Page(CatalogData.Client.Types, limit: CatalogCacheOptions.MaximumPageSize + 1)
        );
    }

    [Fact]
    public void GeneratedSourcesCannotBeSubstitutedWithAnotherPayloadType()
    {
        using TestWorld world = new();
        var catalog = CatalogProvider.Open(new WorldDatabase(world.Path).CatalogPath);
        CatalogSource<long, ClientTypes> substituted = new("client/types");
        Assert.Throws<ArgumentException>(() => catalog.Get(substituted, 601L));
    }

    [Fact]
    public void SharedDefinitionsReplaceRepeatedCatalogReadsWithTheSameResults()
    {
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        const int iterations = 20;
        double? baseline = null;
        using (var direct = SessionPerformance.Begin("direct-catalog", _ => { }, force: true)!)
        {
            for (int index = 0; index < iterations; index++)
            {
                using CatalogDbContext context = new(path);
                baseline = context.Set<SourceRecord>().Find("client/types", "601")!.Read<ClientTypes>().Volume;
                Assert.Equal(15000, baseline);
            }
            Assert.Equal(iterations, direct.Commands);
            Assert.Equal(iterations, direct.Connections);
        }
        var catalog = CatalogProvider.Open(path);
        _ = catalog.Get(CatalogData.Client.Types, 601L);
        using var shared = SessionPerformance.Begin("shared-catalog", _ => { }, force: true)!;
        for (int index = 0; index < iterations; index++)
        {
            Assert.Equal(baseline, catalog.Get(CatalogData.Client.Types, 601L).Volume);
        }
        Assert.Equal(0, shared.Commands);
        Assert.Equal(0, shared.Connections);
    }
}
