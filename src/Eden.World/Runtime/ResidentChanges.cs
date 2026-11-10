using System.Collections;

namespace Eden.World.Runtime;

internal interface IResidentChanges
{
    void Complete(bool commit);
}

// A resident operation may discover a cold dependency only after evolving another
// entity. Retain changed keys, not a copy of the scene, until that operation succeeds.
// A persistence handoff rolls back before releasing the system owner, so rerunning
// typed validation cannot spend a shot twice or lose its returned notification.
internal sealed class ResidentChanges : IDisposable
{
    [ThreadStatic]
    private static ResidentChanges? s_current;
    internal static ResidentChanges? Current => s_current;
    private readonly List<IResidentChanges> _participants = [];
    private bool _committed;

    internal ResidentChanges()
    {
        if (s_current is not null)
        {
            throw new InvalidOperationException("Resident change scopes cannot overlap.");
        }
        s_current = this;
    }

    internal void Enlist(IResidentChanges participant) => _participants.Add(participant);

    internal void Commit() => _committed = true;

    public void Dispose()
    {
        for (int index = _participants.Count - 1; index >= 0; index--)
        {
            _participants[index].Complete(_committed);
        }
        s_current = null;
    }
}

internal sealed class ResidentMap<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>, IResidentChanges
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _values;

    private readonly record struct Original(bool Present, TValue Value);

    private Dictionary<TKey, Original>? _originals;

    public ResidentMap() => _values = [];

    private ResidentMap(Dictionary<TKey, TValue> values) => _values = values;

    public static implicit operator ResidentMap<TKey, TValue>(Dictionary<TKey, TValue> values) => new(values);

    private void Capture(TKey key)
    {
        if (ResidentChanges.Current is not { } changes)
        {
            return;
        }
        if (_originals is null)
        {
            _originals = [];
            changes.Enlist(this);
        }
        if (!_originals.ContainsKey(key))
        {
            bool present = _values.TryGetValue(key, out var value);
            _originals.Add(key, new(present, value!));
        }
    }

    public TValue this[TKey key]
    {
        get => _values[key];
        set
        {
            Capture(key);
            _values[key] = value;
        }
    }

    public ICollection<TKey> Keys => _values.Keys;
    public ICollection<TValue> Values => _values.Values;
    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _values.Keys;
    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _values.Values;
    public int Count => _values.Count;
    public bool IsReadOnly => false;

    public bool ContainsKey(TKey key) => _values.ContainsKey(key);

    public bool TryGetValue(TKey key, out TValue value) => _values.TryGetValue(key, out value!);

    public void Add(TKey key, TValue value)
    {
        Capture(key);
        _values.Add(key, value);
    }

    public bool TryAdd(TKey key, TValue value)
    {
        Capture(key);
        return _values.TryAdd(key, value);
    }

    public bool Remove(TKey key)
    {
        Capture(key);
        return _values.Remove(key);
    }

    public bool Remove(TKey key, out TValue value)
    {
        Capture(key);
        return _values.Remove(key, out value!);
    }

    public void Clear()
    {
        foreach (var key in _values.Keys)
        {
            Capture(key);
        }
        _values.Clear();
    }

    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_values).Contains(item);

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int index) =>
        ((ICollection<KeyValuePair<TKey, TValue>>)_values).CopyTo(array, index);

    public bool Remove(KeyValuePair<TKey, TValue> item) => Contains(item) && Remove(item.Key);

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Complete(bool commit)
    {
        if (!commit && _originals is not null)
        {
            foreach (var (key, original) in _originals)
            {
                if (original.Present)
                {
                    _values[key] = original.Value;
                }
                else
                {
                    _values.Remove(key);
                }
            }
        }
        _originals = null;
    }
}

internal sealed class ResidentSet<T> : ISet<T>, IReadOnlySet<T>, IResidentChanges
    where T : notnull
{
    private readonly HashSet<T> _values;
    private Dictionary<T, bool>? _originals;

    public ResidentSet() => _values = [];

    private ResidentSet(HashSet<T> values) => _values = values;

    public static implicit operator ResidentSet<T>(HashSet<T> values) => new(values);

    private void Capture(T item)
    {
        if (ResidentChanges.Current is not { } changes)
        {
            return;
        }
        if (_originals is null)
        {
            _originals = [];
            changes.Enlist(this);
        }
        _originals.TryAdd(item, _values.Contains(item));
    }

    public bool Add(T item)
    {
        Capture(item);
        return _values.Add(item);
    }

    void ICollection<T>.Add(T item) => Add(item);

    public bool Remove(T item)
    {
        Capture(item);
        return _values.Remove(item);
    }

    public void Clear()
    {
        foreach (var item in _values)
        {
            Capture(item);
        }
        _values.Clear();
    }

    public int Count => _values.Count;
    public bool IsReadOnly => false;

    public bool Contains(T item) => _values.Contains(item);

    public void CopyTo(T[] array, int index) => _values.CopyTo(array, index);

    public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsProperSubsetOf(IEnumerable<T> other) => _values.IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<T> other) => _values.IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<T> other) => _values.IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<T> other) => _values.IsSupersetOf(other);

    public bool Overlaps(IEnumerable<T> other) => _values.Overlaps(other);

    public bool SetEquals(IEnumerable<T> other) => _values.SetEquals(other);

    public void UnionWith(IEnumerable<T> other)
    {
        foreach (var item in other)
        {
            Add(item);
        }
    }

    public void ExceptWith(IEnumerable<T> other)
    {
        foreach (var item in other.ToArray())
        {
            Remove(item);
        }
    }

    public void IntersectWith(IEnumerable<T> other)
    {
        var keep = other.ToHashSet();
        ExceptWith(_values.Where(item => !keep.Contains(item)));
    }

    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        foreach (var item in other.Distinct().ToArray())
        {
            if (!Remove(item))
            {
                Add(item);
            }
        }
    }

    public void Complete(bool commit)
    {
        if (!commit && _originals is not null)
        {
            foreach (var (item, present) in _originals)
            {
                if (present)
                {
                    _values.Add(item);
                }
                else
                {
                    _values.Remove(item);
                }
            }
        }
        _originals = null;
    }
}
