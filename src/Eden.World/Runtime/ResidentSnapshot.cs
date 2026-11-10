using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

// Persistence infrastructure only: each owner supplies an explicit typed view and
// fixed table dependencies. SQL text and arbitrary query results are never cached.
internal sealed class ResidentSnapshot<TKey, TValue>(string name, params string[] tables)
    where TKey : notnull
{
    internal const int MinimumEntries = 512;

    private sealed record Entry(TValue Value, long At, long Until);

    private readonly Dictionary<TKey, Entry> _entries = [];
    internal string Name => name;
    internal bool HasEntries => _entries.Count != 0;

    internal bool TryRead(TKey key, long now, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry) && now >= entry.At && now < entry.Until)
        {
            value = entry.Value;
            return true;
        }
        value = default!;
        return false;
    }

    internal void Publish(TKey key, TValue value, long now, long until, int maximumEntries = MinimumEntries)
    {
        if (_entries.Count >= maximumEntries)
        {
            _entries.Clear();
        }
        _entries[key] = new(value, now, until);
    }

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes, Func<TKey, CombatRowChange, bool>? affects = null)
    {
        var relevant = changes.Where(c => tables.Contains(c.Table, StringComparer.Ordinal)).ToArray();
        if (relevant.Length == 0)
        {
            return;
        }
        if (affects is null)
        {
            Clear();
            return;
        }
        foreach (var key in _entries.Keys.Where(key => relevant.Any(change => affects(key, change))).ToArray())
        {
            _entries.Remove(key);
        }
    }

    internal void Remove(TKey key) => _entries.Remove(key);

    internal void ObserveValues(IReadOnlyCollection<CombatRowChange> changes, Func<TKey, TValue, CombatRowChange, bool> affects)
    {
        var relevant = changes.Where(c => tables.Contains(c.Table, StringComparer.Ordinal)).ToArray();
        foreach (var pair in _entries.Where(p => relevant.Any(c => affects(p.Key, p.Value.Value, c))).ToArray())
        {
            _entries.Remove(pair.Key);
        }
    }

    internal void Clear() => _entries.Clear();
}
