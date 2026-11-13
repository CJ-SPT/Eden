using System.Collections.Frozen;
using Eden.Gameplay.Dogma;
using Eden.World.Fittings;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerFitRetentionTests
{
    [Fact]
    public void FullyPinnedCachesKeepBothLimitsAndPreferDetailedPilots()
    {
        const int profiles = 8192;
        const int snapshots = 512;
        const string revision = "current";
        using ResidentFitCache cache = new(new(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite")));
        var attributes = new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.MaxVelocity] = 100 }.ToFrozenDictionary();
        FittingSnapshot snapshot = new(FrozenDictionary<long, FrozenDictionary<DogmaAttributeId, double>>.Empty, [], null);
        cache.Pin(Enumerable.Range(1, profiles + 1).Select(pilot => (long)pilot));
        cache.Publish(Enumerable.Range(1, profiles).ToDictionary(pilot => Key(pilot), _ => new FitCacheEntry(revision, attributes)));
        cache.PublishSnapshots(
            Enumerable
                .Range(1, snapshots)
                .ToDictionary(pilot => ((long)pilot, (long)pilot), _ => new FittingSnapshotEntry(revision, snapshot))
        );

        const int cold = profiles + 2;
        cache.Publish(new Dictionary<FitCacheKey, FitCacheEntry> { [Key(cold)] = new(revision, attributes) });
        cache.PublishSnapshots(new Dictionary<(long, long), FittingSnapshotEntry> { [(cold, cold)] = new(revision, snapshot) });
        Assert.Null(cache.Read(Key(cold), revision));
        Assert.Null(cache.ReadSnapshot(cold, cold, revision));
        Assert.NotNull(cache.Read(Key(1), revision));
        Assert.Same(snapshot, cache.ReadSnapshot(1, 1, revision));

        cache.Publish(new Dictionary<FitCacheKey, FitCacheEntry> { [Key(profiles + 1)] = new(revision, attributes) });
        cache.PublishSnapshots(
            new Dictionary<(long, long), FittingSnapshotEntry> { [(snapshots + 1, snapshots + 1)] = new(revision, snapshot) }
        );
        Assert.Null(cache.Read(Key(1), revision));
        Assert.Null(cache.ReadSnapshot(1, 1, revision));
        Assert.NotNull(cache.Read(Key(profiles + 1), revision));
        Assert.Same(snapshot, cache.ReadSnapshot(snapshots + 1, snapshots + 1, revision));

        static FitCacheKey Key(long pilot) => new(pilot, pilot, pilot);
    }

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
