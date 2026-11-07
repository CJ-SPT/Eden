using System.Collections.Concurrent;
using System.Collections.Frozen;
using Eden.Gameplay.Dogma;
using Eden.World.Storage;

namespace Eden.World.Fittings;

internal readonly record struct FitCacheKey(long PilotId, long ShipId, long ItemId);

internal sealed record FitCacheEntry(string Revision, FrozenDictionary<DogmaAttributeId, double> Values);

internal sealed record FittingSnapshot(
    FrozenDictionary<long, FrozenDictionary<DogmaAttributeId, double>> Modules,
    IReadOnlyList<Eden.Gameplay.Inventory.FittingSkillBonus> SkillBonuses
);

internal sealed record FittingSnapshotEntry(string Revision, FittingSnapshot Snapshot);

internal sealed class ResidentFitCache : IDisposable
{
    private const int MaximumEntries = 8192;
    private const int MaximumSnapshots = 512;
    private static readonly ConcurrentDictionary<string, WeakReference<ResidentFitCache>> s_worlds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FitCacheKey, FitCacheEntry> _entries = [];
    private readonly Queue<FitCacheKey> _order = [];
    private readonly Dictionary<(long Pilot, long Ship), FittingSnapshotEntry> _snapshots = [];
    private readonly Queue<(long Pilot, long Ship)> _snapshotOrder = [];
    private readonly string _path;
    private readonly object _gate = new();

    internal ResidentFitCache(WorldDatabase database)
    {
        _path = Path.GetFullPath(database.Path);
        s_worlds[_path] = new(this);
    }

    internal static ResidentFitCache? Find(string path) =>
        s_worlds.TryGetValue(Path.GetFullPath(path), out var weak) && weak.TryGetTarget(out var cache) ? cache : null;

    internal Dictionary<DogmaAttributeId, double>? Read(FitCacheKey key, string revision)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) && entry.Revision == revision ? entry.Values.ToDictionary() : null;
        }
    }

    internal void Publish(IReadOnlyDictionary<FitCacheKey, FitCacheEntry> entries)
    {
        lock (_gate)
        {
            foreach (var pair in entries)
            {
                if (!_entries.ContainsKey(pair.Key))
                {
                    if (_entries.Count >= MaximumEntries)
                    {
                        _entries.Remove(_order.Dequeue());
                    }
                    _order.Enqueue(pair.Key);
                }
                _entries[pair.Key] = pair.Value;
            }
        }
    }

    internal FittingSnapshot? ReadSnapshot(long pilot, long ship, string revision)
    {
        lock (_gate)
        {
            return _snapshots.TryGetValue((pilot, ship), out var entry) && entry.Revision == revision ? entry.Snapshot : null;
        }
    }

    internal void PublishSnapshots(IReadOnlyDictionary<(long Pilot, long Ship), FittingSnapshotEntry> entries)
    {
        lock (_gate)
        {
            foreach (var pair in entries)
            {
                if (!_snapshots.ContainsKey(pair.Key))
                {
                    if (_snapshots.Count >= MaximumSnapshots)
                    {
                        _snapshots.Remove(_snapshotOrder.Dequeue());
                    }
                    _snapshotOrder.Enqueue(pair.Key);
                }
                _snapshots[pair.Key] = pair.Value;
            }
        }
    }

    internal static void CatalogChanged(string catalogPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(catalogPath))!;
        foreach (var weak in s_worlds.Values)
        {
            if (weak.TryGetTarget(out var cache) && StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(cache._path), directory))
            {
                lock (cache._gate)
                {
                    cache._entries.Clear();
                    cache._order.Clear();
                    cache._snapshots.Clear();
                    cache._snapshotOrder.Clear();
                }
            }
        }
    }

    public void Dispose()
    {
        if (Find(_path) == this)
        {
            s_worlds.TryRemove(_path, out _);
        }
    }
}
