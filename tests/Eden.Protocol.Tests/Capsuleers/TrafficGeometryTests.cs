using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.World.Runtime;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class TrafficGeometryTests
{
    private const int Capacity = 1024;
    private const int FirstSystem = 30000141;
    private const int FirstBelt = 40000001;
    private const long Catalog = 1;

    [Fact]
    public void FullResourceCachePreservesOtherSystemsOnReplacementAndEvictsOnlyTheLeastRecentView()
    {
        var geometry = FillResourceCache();
        Assert.True(geometry.TryRead(FirstSystem, Catalog, out var original));
        StaticSpaceObject marker = new(FirstBelt + 2, 15, AsteroidDefinitions.BeltGroup, 2, 0, new(100, 0, 0), 10);
        List<StaticSpaceObject> input = [marker];
        geometry.Publish(FirstSystem + 2, Catalog, [FirstBelt + 2], input);
        input.Clear();
        Assert.False(geometry.TryRead(FirstSystem + 2, Catalog + 1, out _));
        Assert.True(geometry.TryRead(FirstSystem + 2, Catalog, out var replacement));
        Assert.Same(marker, Assert.Single(replacement));

        for (int offset = 0; offset < Capacity; offset++)
        {
            Assert.True(geometry.TryRead(FirstSystem + offset, Catalog, out _));
        }
        Assert.True(geometry.TryRead(FirstSystem, Catalog, out var warmed));
        Assert.Same(original, warmed);

        geometry.Publish(FirstSystem + Capacity, Catalog, [FirstBelt + Capacity], []);
        Assert.False(geometry.TryRead(FirstSystem + 1, Catalog, out _));
        Assert.True(geometry.TryRead(FirstSystem, Catalog, out var retained));
        Assert.Same(original, retained);
        Assert.True(geometry.TryRead(FirstSystem + 2, Catalog, out var retainedReplacement));
        Assert.Same(replacement, retainedReplacement);
        for (int offset = 3; offset <= Capacity; offset++)
        {
            Assert.True(geometry.TryRead(FirstSystem + offset, Catalog, out _));
        }
        Assert.Equal(0, geometry.Revision);
    }

    [Fact]
    public void ResourceEvictionKeepsKnownBeltOwnershipAndRespectsResourceAndSchemaInvalidations()
    {
        var geometry = FillResourceCache();
        geometry.Publish(FirstSystem + Capacity, Catalog, [FirstBelt + Capacity], []);
        Assert.False(geometry.TryRead(FirstSystem, Catalog, out _));
        Assert.True(geometry.TryRead(FirstSystem + 1, Catalog, out var before));
        geometry.Observe([new("WorldBeltRenewal", FirstBelt)]);
        Assert.True(geometry.TryRead(FirstSystem + 1, Catalog, out var after));
        Assert.Same(before, after);

        long asteroid = AsteroidDefinitions.FirstAsteroidId + (long)(FirstBelt + 1) * AsteroidDefinitions.IdsPerBelt;
        geometry.Observe([new("WorldAsteroidGeometry", asteroid, Resident: true, System: FirstSystem + 1)]);
        Assert.False(geometry.TryRead(FirstSystem + 1, Catalog, out _));
        geometry.Observe([new("WorldAsteroidGeometry", MissionMiningRules.FirstDepositId, Resident: true, System: FirstSystem + 2)]);
        Assert.True(geometry.TryRead(FirstSystem + 2, Catalog, out _));
        Assert.Equal(2, geometry.Revision);

        geometry.Publish(FirstSystem + Capacity + 1, Catalog, [FirstBelt + Capacity + 1], []);
        geometry.Publish(FirstSystem + Capacity + 2, Catalog, [FirstBelt + Capacity + 2], []);
        Assert.False(geometry.TryRead(FirstSystem + 3, Catalog, out _));
        Assert.True(geometry.TryRead(FirstSystem + 2, Catalog, out _));

        geometry.Observe([new("schema", 0)]);
        Assert.False(geometry.TryRead(FirstSystem + 2, Catalog, out _));
        geometry.Publish(FirstSystem + 1, Catalog, [FirstBelt + 1], []);
        geometry.Observe([new("WorldBeltRenewal", FirstBelt)]);
        Assert.False(geometry.TryRead(FirstSystem + 1, Catalog, out _));
        Assert.Equal(4, geometry.Revision);
    }

    private static LiveTrafficGeometry FillResourceCache()
    {
        LiveTrafficGeometry geometry = new();
        for (int offset = 0; offset < Capacity; offset++)
        {
            geometry.Publish(FirstSystem + offset, Catalog, [FirstBelt + offset], []);
        }
        return geometry;
    }
}
