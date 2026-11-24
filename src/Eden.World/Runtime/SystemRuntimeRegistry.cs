using System.Collections.Concurrent;
using Eden.Infrastructure.Diagnostics;

namespace Eden.World.Runtime;

// Registry admission is separate from simulation admission: creating a runtime must
// never require entering another system. Runtime identities remain stable for the
// lifetime of the live world, including while transactions wait for their owners.
internal sealed class SystemRuntimeRegistry<T>(Func<int, T> create, Func<bool>? canCreate = null)
{
    private readonly ConcurrentDictionary<int, Lazy<Entry>> _systems = new();
    private readonly object _admission = new();

    internal sealed class Entry(int id, T state)
    {
        internal int Id { get; } = id;
        internal T State { get; } = state;
        internal OwnershipMonitor Ownership { get; } = new("system");
        private long _revision;
        internal long Revision => Volatile.Read(ref _revision);

        internal IDisposable Enter(string operation)
        {
            var lease = new Lease(this, Ownership.IsEntered);
            lease.Ownership = Ownership.Enter(operation, Id);
            return lease;
        }

        internal IDisposable? TryEnter(string operation)
        {
            var lease = new Lease(this, Ownership.IsEntered);
            lease.Ownership = Ownership.TryEnter(operation, Id);
            return lease.Ownership is null ? null : lease;
        }

        private sealed class Lease(Entry entry, bool nested) : IDisposable
        {
            internal IDisposable? Ownership;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref Ownership, null) is { } held)
                {
                    if (!nested)
                    {
                        Interlocked.Increment(ref entry._revision);
                    }
                    held.Dispose();
                }
            }
        }
    }

    internal IReadOnlyList<Entry> Existing(IEnumerable<int> systems) =>
        systems
            .Distinct()
            .Order()
            .Select(id => _systems.TryGetValue(id, out var entry) && entry.IsValueCreated ? entry.Value : null)
            .OfType<Entry>()
            .ToArray();

    internal Entry Get(int system)
    {
        if (_systems.TryGetValue(system, out var existing))
        {
            return existing.Value;
        }
        if (canCreate?.Invoke() == false)
        {
            throw new InvalidOperationException("Release the current system owner before admitting another runtime.");
        }
        lock (_admission)
        {
            return _systems.GetOrAdd(system, id => new(() => new Entry(id, create(id)))).Value;
        }
    }

    internal IDisposable FreezeAdmissions()
    {
        var lease = new AdmissionLease(_admission);
        Monitor.Enter(_admission);
        return lease;
    }

    private sealed class AdmissionLease(object gate) : IDisposable
    {
        public void Dispose() => Monitor.Exit(gate);
    }

    internal IReadOnlyList<Entry> Snapshot() =>
        _systems.Values.Where(value => value.IsValueCreated).Select(value => value.Value).OrderBy(entry => entry.Id).ToArray();

    internal IDisposable Enter(IEnumerable<int> systems, string operation)
    {
        var entries = systems.Distinct().Order().Select(Get).ToArray();
        List<IDisposable> leases = new(entries.Length);
        var group = new GroupLease(leases);
        try
        {
            foreach (var entry in entries)
            {
                leases.Add(entry.Enter(operation));
            }
            return group;
        }
        catch
        {
            group.Dispose();
            throw;
        }
    }

    internal IDisposable? TryEnter(IEnumerable<int> systems, string operation)
    {
        var ordered = systems.Distinct().Order().ToArray();
        List<IDisposable> leases = new(ordered.Length);
        var group = new GroupLease(leases);
        try
        {
            foreach (int system in ordered)
            {
                if (Get(system).TryEnter(operation) is not { } lease)
                {
                    group.Dispose();
                    return null;
                }
                leases.Add(lease);
            }
            return group;
        }
        catch
        {
            group.Dispose();
            throw;
        }
    }

    private sealed class GroupLease(List<IDisposable> leases) : IDisposable
    {
        public void Dispose()
        {
            for (int index = leases.Count - 1; index >= 0; index--)
            {
                leases[index].Dispose();
            }
            leases.Clear();
        }
    }
}
