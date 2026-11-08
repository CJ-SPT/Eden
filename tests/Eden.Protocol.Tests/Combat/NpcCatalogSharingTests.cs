using Eden.Data.Catalog;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcCatalogSharingTests
{
    [Fact]
    public void IndependentNpcReadersShareImmutableProfilesAndCacheMissingMiningHulls()
    {
        using NpcCombatTests.Fixture fixture = new();
        string path = new WorldDatabase(fixture.Db.Path).CatalogPath;
        using CatalogDbContext first = new(path);
        NpcProfileCatalog reader = new();
        var profile = reader.Read(first, fixture.Npc.TypeId);
        Assert.IsType<CatalogItemDefinition>(profile.Type);
        var mining = fixture.Db.Services.GetRequiredService<NpcMiningCatalog>();
        Assert.Null(mining.Hull(first, int.MaxValue));
        using var metrics = SessionPerformance.Begin("shared-npc-definitions", _ => { }, force: true)!;
        for (int operation = 0; operation < 3; operation++)
        {
            using CatalogDbContext next = new(path);
            Assert.Same(profile, new NpcProfileCatalog().Read(next, fixture.Npc.TypeId));
            Assert.Null(mining.Hull(next, int.MaxValue));
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void WritableCatalogInvalidatesNpcProfilesWithoutChangingEarlierSnapshots()
    {
        using NpcCombatTests.Fixture fixture = new();
        string path = new WorldDatabase(fixture.Db.Path).CatalogPath;
        using CatalogDbContext first = new(path);
        NpcProfileCatalog reader = new();
        var profile = reader.Read(first, fixture.Npc.TypeId);
        double mass = profile.Type.Mass!.Value;
        using (CatalogDbContext writable = new(path, writable: true))
        {
            writable.Database.ExecuteSql($"UPDATE ItemType SET Mass={mass + 123} WHERE Id={fixture.Npc.TypeId}");
        }
        using CatalogDbContext next = new(path);
        var changed = reader.Read(next, fixture.Npc.TypeId);
        Assert.NotSame(profile, changed);
        Assert.Equal(mass + 123, changed.Type.Mass);
        Assert.Equal(mass, profile.Type.Mass);
        using var metrics = SessionPerformance.Begin("changed-npc-definitions", _ => { }, force: true)!;
        Assert.Same(changed, new NpcProfileCatalog().Read(next, fixture.Npc.TypeId));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
    }

    [Fact]
    public void ActiveSpawnPoolsSurviveCatalogCachePressureWithoutReloading()
    {
        using TestWorld world = new();
        RegionalNpcTests.Prepare(world);
        var database = new WorldDatabase(world.Path);
        var provider = CatalogProvider.Open(database.CatalogPath, new CatalogCacheOptions { MaximumEstimatedBytes = 1024 * 1024 });
        NpcSpawnCatalog reader = new(database);
        var profiles = reader.Profiles;
        Assert.NotEmpty(profiles);
        using var metrics = SessionPerformance.Begin("resident-spawn-pools", _ => { }, force: true)!;
        for (int read = 0; read < 3; read++)
        {
            Assert.Same(profiles[0], reader.Profiles[0]);
            Assert.NotEmpty(reader.Pool(profiles[0].Faction, profiles[0].Hull));
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
        Assert.InRange(provider.Statistics.EstimatedBytes, 0, 1024 * 1024);
    }

    [Fact]
    public void UnpublishedSerpentisCruiserTemplateIsExcludedFromRegionalSpawns()
    {
        const int templateType = 33352;
        using TestWorld world = new();
        RegionalNpcTests.Prepare(world);
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath);
        Assert.Equal("Serpentis Cruiser", catalog.RequireItem(templateType).Name);
        Assert.Equal(0, catalog.Read(CatalogData.Client.Types, (long)templateType)!.Published);
        Assert.Empty(catalog.Definitions.Attributes(templateType));
        Assert.Contains(templateType, Eden.Gameplay.Combat.NpcSpecialTypes.Excluded);
        Assert.DoesNotContain(new NpcSpawnCatalog(new(world.Path)).Profiles, profile => profile.TypeId == templateType);
    }

    [Fact]
    public void ActiveSpawnPoolsRefreshAfterCatalogEditsAndPreservePreviousProfiles()
    {
        using TestWorld world = new();
        RegionalNpcTests.Prepare(world);
        var database = new WorldDatabase(world.Path);
        NpcSpawnCatalog reader = new(database);
        var original = reader.Profiles[0];
        using (CatalogDbContext writable = new(database.CatalogPath, writable: true))
        {
            writable.Database.ExecuteSql($"UPDATE ItemType SET Mass={original.Mass + 123} WHERE Id={original.TypeId}");
        }
        var changed = reader.Profiles.Single(p => p.TypeId == original.TypeId);
        Assert.NotSame(original, changed);
        Assert.Equal(original.Mass + 123, changed.Mass);
        using var metrics = SessionPerformance.Begin("refreshed-spawn-pools", _ => { }, force: true)!;
        Assert.Same(changed, reader.Pool(changed.Faction, changed.Hull).Single(p => p.TypeId == changed.TypeId));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
