using Eden.Infrastructure.Diagnostics;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionCatalogLookupTests
{
    [Fact]
    public void ColdLookupLoadsOnlyRequestedMissionAndSharesItWithTheFullDirectory()
    {
        using MissionFixture fixture = new();
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        MissionProfile profile;
        using (var metrics = SessionPerformance.Begin("cold mission lookup", _ => { }, force: true)!)
        {
            profile = MissionCatalog.Find(catalog, 2912);
            Assert.Equal(2912, profile.Definition.MissionId);
            Assert.InRange(metrics.Commands, 1, 100);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (var metrics = SessionPerformance.Begin("warm mission lookup", _ => { }, force: true)!)
        {
            Assert.Same(profile, MissionCatalog.Find(catalog, 2912));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        Assert.Same(profile, MissionCatalog.Read(catalog).Single(p => p.Definition.MissionId == 2912));
        Assert.Throws<InvalidDataException>(() => MissionCatalog.Find(catalog, int.MaxValue));
        string path = new WorldDatabase(fixture.Db.Path).CatalogPath;
        using (CatalogDbContext changed = new(path, writable: true))
        {
            string key = profile.NameId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string data = "{\"text\":\"Updated mission\"}";
            changed.Database.ExecuteSql(
                $"""
                UPDATE SourceRecord SET Data={data}
                    WHERE Dataset='client/localization_fsd_en-us' AND Key={key}
                """
            );
        }
        using CatalogDbContext updated = new(path);
        Assert.NotSame(profile, MissionCatalog.Find(updated, 2912));
        Assert.Equal("Updated mission", MissionCatalog.Find(updated, 2912).Name);
    }
}
