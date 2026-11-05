namespace Eden.Server.Persistence;

// Persistence infrastructure only: each owner supplies an explicit typed view and
// fixed table dependencies. SQL text and arbitrary query results are never cached.
internal sealed class ResidentSnapshot<TKey, TValue>(string name, params string[] tables)
    where TKey : notnull
{
    private const int MaximumEntries = 512;

    private sealed record Entry(TValue Value, long At, long Until);

    private readonly Dictionary<TKey, Entry> _entries = [];
    internal string Name => name;

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

    internal void Publish(TKey key, TValue value, long now, long until)
    {
        if (_entries.Count >= MaximumEntries)
        {
            _entries.Clear();
        }
        _entries[key] = new(value, now, until);
    }

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (changes.Any(c => tables.Contains(c.Table, StringComparer.Ordinal)))
        {
            Clear();
        }
    }

    internal void Clear() => _entries.Clear();
}
