using System.Collections.Concurrent;
using System.Collections.Frozen;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Storage;

namespace Eden.World.Fittings;

internal readonly record struct FitCacheKey(long PilotId, long ShipId, long ItemId);

internal sealed record FitCacheEntry(string Revision, FrozenDictionary<DogmaAttributeId, double> Values);

internal sealed record MissileFittingSnapshot(
    FrozenDictionary<long, FrozenDictionary<DogmaAttributeId, double>> Charges,
    HullDefense Defense,
    double MaximumTargetRange,
    double DamageMultiplier
);

internal sealed record FittingSnapshot(
    FrozenDictionary<long, FrozenDictionary<DogmaAttributeId, double>> Modules,
    IReadOnlyList<FittingSkillBonus> SkillBonuses,
    double DroneControlLimit,
    double DroneControlDistance,
    MissileFittingSnapshot? Missiles
)
{
    internal double DroneCapacity { get; init; }
    internal double DroneBandwidth { get; init; }

    internal static FittingSnapshot Capture(
        DogmaGraph graph,
        long pilot,
        long ship,
        IReadOnlyList<FittingLocation> modules,
        IReadOnlyList<FittingLocation> charges,
        FittingSkillBonus[] bonuses
    )
    {
        var missiles = charges
            .Where(charge => MissileDefinitions.ChargeGroups.Contains(charge.GroupId))
            .ToFrozenDictionary(charge => charge.Item.Id, charge => graph.Attributes(charge.Item.Id).ToFrozenDictionary());
        return new(
            modules.ToFrozenDictionary(module => module.Item.Id, module => graph.Attributes(module.Item.Id).ToFrozenDictionary()),
            Array.AsReadOnly(bonuses),
            graph.Read(pilot, DogmaAttributeId.MaxActiveDrones),
            graph.Read(pilot, DogmaAttributeId.DroneControlDistance),
            missiles.Count == 0
                ? null
                : new(
                    missiles,
                    ShipHealth.DefenseFrom(graph.Attributes(ship)),
                    graph.Read(ship, DogmaAttributeId.MaxTargetRange),
                    graph.Read(pilot, DogmaAttributeId.MissileDamageMultiplier)
                )
        )
        {
            DroneCapacity = graph.Read(ship, DogmaAttributeId.DroneCapacity),
            DroneBandwidth = graph.Read(ship, DogmaAttributeId.DroneBandwidth),
        };
    }
}

internal sealed record FittingSnapshotEntry(string Revision, FittingSnapshot Snapshot);

internal sealed class ResidentFitCache : IDisposable
{
    private const int MaximumAttributeEntries = 24 * 1024;
    private const long MaximumAttributeBytes = 48 * 1024 * 1024;

    // Bound the frozen keys, values and indexes, plus the cache key, entry and revision.
    private const long EstimatedAttributeBytes = 24;
    private const long EstimatedEntryBytes = 256;
    private const int MaximumSnapshots = 512;
    private static readonly ConcurrentDictionary<string, WeakReference<ResidentFitCache>> s_worlds = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct AttributeEntry(FitCacheEntry Fit, long Bytes, LinkedListNode<FitCacheKey> Recency);

    private readonly Dictionary<FitCacheKey, AttributeEntry> _entries = [];
    private readonly LinkedList<FitCacheKey> _order = new();
    private readonly Dictionary<(long Pilot, long Ship), FittingSnapshotEntry> _snapshots = [];
    private readonly Queue<(long Pilot, long Ship)> _snapshotOrder = [];
    private readonly string _path;
    private readonly object _gate = new();
    private HashSet<long> _pinned = [];
    private long _attributeBytes;

    internal void Pin(IEnumerable<long> pilots)
    {
        lock (_gate)
        {
            _pinned = pilots.ToHashSet();
        }
    }

    internal void ReleasePilot(long pilot)
    {
        lock (_gate)
        {
            if (_pinned.Contains(pilot))
            {
                return;
            }
            foreach (var key in _entries.Keys.Where(k => k.PilotId == pilot).ToArray())
            {
                RemoveAttributes(key);
            }
            foreach (var key in _snapshots.Keys.Where(k => k.Pilot == pilot).ToArray())
            {
                _snapshots.Remove(key);
            }
            var snapshots = _snapshotOrder.Where(_snapshots.ContainsKey).ToArray();
            _snapshotOrder.Clear();
            foreach (var key in snapshots)
            {
                _snapshotOrder.Enqueue(key);
            }
        }
    }

    private bool MakeRoom<TKey, TValue>(Dictionary<TKey, TValue> entries, Queue<TKey> order, Func<TKey, long> pilotId, long incomingPilot)
        where TKey : notnull
    {
        for (int remaining = order.Count; remaining > 0; remaining--)
        {
            var key = order.Dequeue();
            if (_pinned.Contains(pilotId(key)))
            {
                order.Enqueue(key);
            }
            else
            {
                entries.Remove(key);
                return true;
            }
        }
        // Under pressure from detailed pilots, admit new detailed work within the same limit.
        if (_pinned.Contains(incomingPilot))
        {
            entries.Remove(order.Dequeue());
            return true;
        }
        return false;
    }

    internal ResidentFitCache(WorldDatabase database)
    {
        _path = Path.GetFullPath(database.Path);
        s_worlds[_path] = new(this);
    }

    internal static ResidentFitCache? Find(string path) =>
        (s_worlds.TryGetValue(path, out var weak) || s_worlds.TryGetValue(Path.GetFullPath(path), out weak))
        && weak.TryGetTarget(out var cache)
            ? cache
            : null;

    internal Dictionary<DogmaAttributeId, double>? Read(FitCacheKey key, string revision)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Fit.Revision != revision)
            {
                return null;
            }
            if (entry.Recency.Next is not null)
            {
                _order.Remove(entry.Recency);
                _order.AddLast(entry.Recency);
            }
            return entry.Fit.Values.ToDictionary();
        }
    }

    internal void Publish(IReadOnlyDictionary<FitCacheKey, FitCacheEntry> entries)
    {
        lock (_gate)
        {
            foreach (var (key, fit) in entries)
            {
                LinkedListNode<FitCacheKey>? recency = null;
                if (_entries.TryGetValue(key, out var previous))
                {
                    recency = previous.Recency;
                    RemoveAttributes(key);
                }
                long bytes = EstimatedEntryBytes + fit.Values.Count * EstimatedAttributeBytes;
                if (bytes > MaximumAttributeBytes || !MakeAttributeRoom(key.PilotId, bytes))
                {
                    continue;
                }
                recency ??= new(key);
                _order.AddLast(recency);
                _entries.Add(key, new(fit, bytes, recency));
                _attributeBytes += bytes;
            }
        }
    }

    private bool MakeAttributeRoom(long pilot, long bytes)
    {
        while (_entries.Count >= MaximumAttributeEntries || _attributeBytes + bytes > MaximumAttributeBytes)
        {
            var oldest = _order.First;
            var unpinned = oldest;
            while (unpinned is not null && _pinned.Contains(unpinned.Value.PilotId))
            {
                unpinned = unpinned.Next;
            }
            if (unpinned is null && !_pinned.Contains(pilot))
            {
                return false;
            }
            // Observed fits displace other observed fits only when they occupy the entire cache.
            RemoveAttributes((unpinned ?? oldest!).Value);
        }
        return true;
    }

    private void RemoveAttributes(FitCacheKey key)
    {
        if (_entries.Remove(key, out var entry))
        {
            _order.Remove(entry.Recency);
            _attributeBytes -= entry.Bytes;
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
                    if (
                        _snapshots.Count >= MaximumSnapshots
                        && !MakeRoom(_snapshots, _snapshotOrder, static key => key.Pilot, pair.Key.Pilot)
                    )
                    {
                        continue;
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
                    cache._attributeBytes = 0;
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
