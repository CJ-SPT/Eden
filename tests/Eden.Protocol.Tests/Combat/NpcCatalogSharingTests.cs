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
}
