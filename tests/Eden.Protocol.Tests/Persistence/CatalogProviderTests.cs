using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CatalogProviderTests
{
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
            Assert.Same(original, CatalogProvider.Open(path));
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
