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
    MissileFittingSnapshot? Missiles
)
{
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
            missiles.Count == 0
                ? null
                : new(
                    missiles,
                    ShipHealth.DefenseFrom(graph.Attributes(ship)),
                    graph.Read(ship, DogmaAttributeId.MaxTargetRange),
                    graph.Read(pilot, DogmaAttributeId.MissileDamageMultiplier)
                )
        );
    }
}

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
    private HashSet<long> _pinned = [];

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
                _entries.Remove(key);
            }
            foreach (var key in _snapshots.Keys.Where(k => k.Pilot == pilot).ToArray())
            {
                _snapshots.Remove(key);
            }
            var order = _order.Where(_entries.ContainsKey).ToArray();
            _order.Clear();
            foreach (var key in order)
            {
                _order.Enqueue(key);
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
                    if (_entries.Count >= MaximumEntries && !MakeRoom(_entries, _order, static key => key.PilotId, pair.Key.PilotId))
                    {
                        continue;
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
