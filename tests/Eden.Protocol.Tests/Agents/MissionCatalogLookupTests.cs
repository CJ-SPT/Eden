using Eden.Gameplay.Agents;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionCatalogLookupTests
{
    [Fact]
    public void ColdAgentPoolLoadsOnlyItsLevelAndStorylineProfiles()
    {
        using MissionFixture fixture = new();
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        var before = catalog.Definitions.Statistics;
        var selected = MissionCatalog.ForLevel(catalog, 4, storyline: true).ToArray();
        long loads = catalog.Definitions.Statistics.Loads - before.Loads;
        Assert.NotEmpty(selected);
        Assert.All(
            selected,
            profile =>
            {
                Assert.Equal(4, profile.Definition.Level);
                Assert.True(profile.Definition.Storyline);
            }
        );
        Assert.InRange(loads, 1, 150);
        Assert.Equal(
            MissionCatalog
                .Read(catalog)
                .Where(profile =>
                    profile.Definition.Level == 4 && profile.Definition.Storyline && profile.Definition.FixedAgentIds.IsEmpty
                ),
            selected
        );
        for (int level = 1; level <= MissionRules.MaximumAgentLevel; level++)
        {
            foreach (bool storyline in new[] { false, true })
            {
                var expected = MissionCatalog
                    .Read(catalog)
                    .Where(profile =>
                        profile.Definition.Level == level
                        && profile.Definition.Storyline == storyline
                        && profile.Definition.FixedAgentIds.IsEmpty
                    );
                Assert.Equal(expected, MissionCatalog.ForLevel(catalog, level, storyline));
            }
        }
    }

    [Fact]
    public void FixedAgentLookupLoadsItsNativeAssignmentWithoutAddingItToAnyRotatingPool()
    {
        using MissionFixture fixture = new();
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        var before = catalog.Definitions.Statistics;
        var profile = Assert.Single(MissionCatalog.ForFixedAgent(catalog, 3018480));
        Assert.Equal(1752, profile.Definition.MissionId);
        Assert.InRange(catalog.Definitions.Statistics.Loads - before.Loads, 1, 30);
        Assert.Same(profile, Assert.Single(MissionCatalog.ForFixedAgent(catalog, 3018481)));
        Assert.Empty(MissionCatalog.ForFixedAgent(catalog, 3018344));
        Assert.DoesNotContain(MissionCatalog.ForLevel(catalog, 1, true), p => p.Definition.MissionId == 1752);
    }

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
