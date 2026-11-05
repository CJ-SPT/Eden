using Eden.Protocol.PilotSessions;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CatalogFittingReadTests
{
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
    public async Task TickScopeSharesOnlyCatalogInputsAcrossContextsAndExpiresAtTheNextOperation()
    {
        using TestWorld db = new();
        string path = new WorldDatabase(db.Path).CatalogPath;
        CatalogFittingReadScope scopes = new();
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
            Assert.Equal(3, metrics.Commands);
        }
        using (scopes.Begin())
        {
            using CatalogDbContext next = new(path);
            Assert.Null(next.FindSource("missing", "key"));
            Assert.NotEmpty(next.FittingAttributes(601));
            Assert.NotNull(next.FittingType(601));
        }
        Assert.Equal(6, metrics.Commands);
    }

    [Fact]
    public void ReadOnlyOperationReusesMissingSourcesAndAttributesWithoutSharingMutableCopies()
    {
        using TestWorld db = new();
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        using var metrics = SessionPerformance.Begin("catalog", _ => { }, force: true)!;
        Assert.Null(catalog.FindSource("missing", "key"));
        Assert.Null(catalog.FindSource("missing", "key"));
        var first = catalog.FittingAttributes(601);
        var modified = first.ToDictionary();
        var key = first.Keys.First();
        modified[key]++;
        Assert.Equal(first[key], catalog.FittingAttributes(601)[key]);
        Assert.Equal(2, metrics.Commands);
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
