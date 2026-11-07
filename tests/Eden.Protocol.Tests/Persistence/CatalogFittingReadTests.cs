using Eden.Data.Catalog;
using Eden.Data.World.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CatalogFittingReadTests
{
    [Fact]
    public void SharedEffectsPreserveSourceOrderAndRemoveRepeatedIds()
    {
        using TestWorld db = new();
        string path = new WorldDatabase(db.Path).CatalogPath;
        using (CatalogDbContext fixture = new(path, writable: true))
        {
            fixture
                .Set<SourceRecord>()
                .AsTracking()
                .Single(record => record.Dataset == "client/typedogma" && record.Key == "3651")
                .Data = """
                {"dogmaEffects":[{"effectID":67},{"effectID":42},{"effectID":12},{"effectID":16},{"effectID":42}]}
                """;
            fixture.SaveChanges();
        }
        using CatalogDbContext catalog = new(path);
        Assert.Equal(new long?[] { 67, 42, 12, 16 }, PassiveCatalog.Effects(catalog, 3651).Select(effect => effect.EffectID));
    }

    [Fact]
    public void StaticFittingDefinitionsAndMissingProfilesRemainSharedAcrossOperations()
    {
        using TestWorld db = new();
        string path = new WorldDatabase(db.Path).CatalogPath;
        CatalogReadScope scopes = new();
        using CatalogDbContext first = new(path);
        var module = FittingCatalog.Definition(first, 3651);
        Assert.NotNull(module);
        var effects = FittingCatalog.Effects(first, 3651);
        var modifiers = PassiveCatalog.Modifiers(first, 3651);
        Assert.Null(FittingCatalog.Definition(first, int.MaxValue));
        using var metrics = SessionPerformance.Begin("shared-fitting-definitions", _ => { }, force: true)!;
        for (int operation = 0; operation < 3; operation++)
        {
            using var scope = scopes.Begin();
            using CatalogDbContext next = new(path);
            Assert.Same(module, FittingCatalog.Definition(next, 3651));
            Assert.Same(effects, FittingCatalog.Effects(next, 3651));
            Assert.Equal(modifiers, PassiveCatalog.Modifiers(next, 3651));
            Assert.Null(FittingCatalog.Definition(next, int.MaxValue));
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void WritableFixtureBypassesDerivedCachesAndInvalidatesTheNextGeneration()
    {
        using TestWorld db = new();
        string path = new WorldDatabase(db.Path).CatalogPath;
        using CatalogDbContext original = new(path);
        var before = FittingCatalog.Definition(original, 3651);
        Assert.NotNull(before);
        long generation = original.Definitions.Identity.Generation;
        using (CatalogDbContext fixture = new(path, writable: true))
        {
            fixture.Set<SourceRecord>().AsTracking().Single(record => record.Dataset == "client/typedogma" && record.Key == "3651").Data =
                "{\"dogmaEffects\":[]}";
            fixture.SaveChanges();
            Assert.Null(FittingCatalog.Definition(fixture, 3651));
            Assert.Empty(FittingCatalog.Effects(fixture, 3651));
        }
        using CatalogDbContext reopened = new(path);
        Assert.NotEqual(generation, reopened.Definitions.Identity.Generation);
        Assert.Null(FittingCatalog.Definition(reopened, 3651));
        Assert.Empty(FittingCatalog.Effects(reopened, 3651));
        Assert.NotNull(before);
    }

    [Fact]
    public async Task HostOperationSharesFittingInputsAndOneConnectionAcrossAwaitAndNestedScopes()
    {
        using TestWorld db = new();
        string path = new WorldDatabase(db.Path).CatalogPath;
        var scopes = db.Services.GetRequiredService<IPilotOperationScope>();
        CatalogProvider.Open(path).Attributes(601);
        using var metrics = SessionPerformance.Begin("operation", _ => { }, force: true)!;
        using (scopes.Begin())
        {
            using (CatalogDbContext first = new(path))
            {
                Assert.NotEmpty(first.FittingAttributes(601));
            }
            await Task.Yield();
            using (scopes.Begin())
            using (CatalogDbContext nested = new(path))
            {
                Assert.NotEmpty(nested.FittingAttributes(601));
                Assert.NotNull(nested.Set<ItemType>().Find(601));
            }
            using CatalogDbContext last = new(path);
            Assert.NotNull(last.Set<ItemType>().Find(601));
            Assert.Equal(2, metrics.Commands);
            Assert.Equal(1, metrics.Connections);
        }
        using (scopes.Begin())
        using (CatalogDbContext next = new(path))
        {
            Assert.NotEmpty(next.FittingAttributes(601));
        }
        Assert.Equal(2, metrics.Commands);
        Assert.Equal(1, metrics.Connections);
    }

    [Fact]
    public void BatchedSkillLevelsMatchSingleReadsIncludingMissingAndPendingSkillChanges()
    {
        using TestWorld db = new();
        using WorldDbContext world = new(db.Path);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        var trained = world.Set<PilotSkill>().Where(s => s.PilotId == 90000001).First();
        int[] skills = [trained.TypeId, int.MaxValue];
        var batch = PilotSkillReader.Levels(world, catalog, trained.PilotId, skills);
        foreach (int skill in skills)
        {
            Assert.Equal(PilotSkillReader.Level(world, catalog, trained.PilotId, skill), batch[skill]);
        }
        trained.Points = 0;
        var changed = PilotSkillReader.Levels(world, catalog, trained.PilotId, skills);
        Assert.Equal(PilotSkillReader.Level(world, catalog, trained.PilotId, trained.TypeId), changed[trained.TypeId]);
        Assert.Equal(0, changed[trained.TypeId]);
        world.Add(
            new PilotSkill
            {
                PilotId = trained.PilotId,
                TypeId = int.MaxValue,
                Points = 250,
            }
        );
        var added = PilotSkillReader.Levels(world, catalog, trained.PilotId, skills);
        Assert.Equal(PilotSkillReader.Level(world, catalog, trained.PilotId, int.MaxValue), added[int.MaxValue]);
        Assert.Equal(1, added[int.MaxValue]);
    }

    [Fact]
    public async Task TickScopeRetainsTypedDefinitionsWhileLegacySourcesExpireAtTheNextOperation()
    {
        using TestWorld db = new();
        string path = new WorldDatabase(db.Path).CatalogPath;
        CatalogFittingReadScope scopes = new();
        var shared = CatalogProvider.Open(path);
        shared.Attributes(601);
        Assert.NotEmpty(shared.Items);
        using var metrics = SessionPerformance.Begin("operation", _ => { }, force: true)!;
        using (scopes.Begin())
        {
            using (CatalogDbContext first = new(path))
            {
                Assert.Null(first.FindSource("missing", "key"));
                Assert.NotEmpty(first.FittingAttributes(601));
                Assert.NotNull(first.FittingType(601));
            }
            await Task.Yield();
            using CatalogDbContext second = new(path);
            Assert.Null(second.FindSource("missing", "key"));
            Assert.NotEmpty(second.FittingAttributes(601));
            Assert.NotNull(second.FittingType(601));
            Assert.Equal(1, metrics.Commands);
        }
        using (scopes.Begin())
        {
            using CatalogDbContext next = new(path);
            Assert.Null(next.FindSource("missing", "key"));
            Assert.NotEmpty(next.FittingAttributes(601));
            Assert.NotNull(next.FittingType(601));
        }
        Assert.Equal(2, metrics.Commands);
    }

    [Fact]
    public void ReadOnlyOperationReusesMissingSourcesAndAttributesWithoutSharingMutableCopies()
    {
        using TestWorld db = new();
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        var first = catalog.FittingAttributes(601);
        using var metrics = SessionPerformance.Begin("catalog", _ => { }, force: true)!;
        Assert.Null(catalog.FindSource("missing", "key"));
        Assert.Null(catalog.FindSource("missing", "key"));
        var modified = first.ToDictionary();
        var key = first.Keys.First();
        modified[key]++;
        Assert.Equal(first[key], catalog.FittingAttributes(601)[key]);
        Assert.Equal(1, metrics.Commands);
    }

    [Fact]
    public void WritableOperationAndNewReadOnlyContextObserveCatalogEdits()
    {
        using TestWorld db = new();
        string path = new WorldDatabase(db.Path).CatalogPath;
        using (CatalogDbContext catalog = new(path, writable: true))
        {
            Assert.Null(catalog.FindSource("performance", "key"));
            catalog.Add(
                new SourceRecord
                {
                    Dataset = "performance",
                    Key = "key",
                    Data = "{}",
                }
            );
            catalog.SaveChanges();
            catalog.ChangeTracker.Clear();
            Assert.Equal("{}", catalog.FindSource("performance", "key")!.Data);
            catalog.Database.ExecuteSqlRaw("UPDATE SourceRecord SET Data='[]' WHERE Dataset='performance' AND Key='key'");
            Assert.Equal("[]", catalog.FindSource("performance", "key")!.Data);
            var attribute = catalog.FittingAttributes(601).First();
            catalog.Database.ExecuteSql(
                $"""
                UPDATE TypeAttribute SET Value={attribute.Value + 123}
                    WHERE ItemTypeId=601 AND AttributeId={(int)attribute.Key}
                """
            );
            Assert.Equal(attribute.Value + 123, catalog.FittingAttributes(601)[attribute.Key]);
        }
        using CatalogDbContext reopened = new(path);
        Assert.Equal("[]", reopened.FindSource("performance", "key")!.Data);
    }
}
