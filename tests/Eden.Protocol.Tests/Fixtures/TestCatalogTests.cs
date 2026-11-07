using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Fixtures;

public sealed class TestCatalogTests
{
    [Fact]
    public void CachedSeedsKeepCatalogEditsAndWorldStateIsolated()
    {
        const int rifterType = 587;
        using TestWorld first = new();
        using TestWorld second = new();
        string firstPath = new WorldDatabase(first.Path).CatalogPath;
        string secondPath = new WorldDatabase(second.Path).CatalogPath;
        PassiveCatalogFixture.LoadSmallHull(firstPath, rifterType);
        PassiveCatalogFixture.LoadSmallHull(secondPath, rifterType);
        string key = PassiveCatalog.Key(rifterType);
        string original;
        using (CatalogDbContext catalog = new(secondPath))
        {
            original = catalog.Set<SourceRecord>().Find("client/typedogma", key)!.Data;
        }
        using (CatalogDbContext catalog = new(firstPath, writable: true))
        {
            string empty = "{\"dogmaEffects\":[]}";
            catalog.Database.ExecuteSql($"UPDATE SourceRecord SET Data={empty} WHERE Dataset='client/typedogma' AND Key={key}");
        }
        using (WorldDbContext world = new(first.Path))
        {
            world.Pilots.Single(p => p.AccountId == 1000001).Name = "Changed fixture";
            world.SaveChanges();
        }
        using (CatalogDbContext catalog = new(secondPath))
        {
            Assert.Equal(original, catalog.Set<SourceRecord>().Find("client/typedogma", key)!.Data);
        }
        Assert.Equal("Eden Pilot", second.Store.FindPilot(1000001)!.Name);
        Assert.Throws<IOException>(() => TestCatalog.Create(secondPath));
        using TestWorld later = new();
        string laterPath = new WorldDatabase(later.Path).CatalogPath;
        PassiveCatalogFixture.LoadSmallHull(laterPath, rifterType);
        using CatalogDbContext laterCatalog = new(laterPath);
        Assert.Equal(original, laterCatalog.Set<SourceRecord>().Find("client/typedogma", key)!.Data);
        Assert.Equal("Eden Pilot", later.Store.FindPilot(1000001)!.Name);
    }
}
