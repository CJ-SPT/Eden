using System.Collections.Frozen;
using Eden.Gameplay.Dogma;
using Eden.World.Fittings;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerFitRetentionTests
{
    [Fact]
    public void CapacityEvictsColdProfilesSelectivelyAndPinsDetailedPilots()
    {
        using ResidentFitCache cache = new(new(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        const string Revision = "unchanged";
        var attributes = new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.MaxVelocity] = 100 }.ToFrozenDictionary();
        FitCacheKey hot = new(1, 1, 1);
        FitCacheKey cold = new(2, 2, 2);
        cache.Pin([1]);
        cache.Publish(new Dictionary<FitCacheKey, FitCacheEntry> { [hot] = new(Revision, attributes), [cold] = new(Revision, attributes) });
        for (int pilot = 3; pilot <= 8194; pilot++)
        {
            cache.Publish(new Dictionary<FitCacheKey, FitCacheEntry> { [new(pilot, pilot, pilot)] = new(Revision, attributes) });
        }
        Assert.Equal(100, cache.Read(hot, Revision)![DogmaAttributeId.MaxVelocity]);
        Assert.Null(cache.Read(cold, Revision));
        Assert.NotNull(cache.Read(new(8194, 8194, 8194), Revision));
        cache.ReleasePilot(1);
        Assert.NotNull(cache.Read(hot, Revision));
        cache.Pin([]);
        cache.ReleasePilot(1);
        Assert.Null(cache.Read(hot, Revision));
        Assert.NotNull(cache.Read(new(8194, 8194, 8194), Revision));
    }
}
