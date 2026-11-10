using System.Collections.Concurrent;
using Eden.Infrastructure.Diagnostics;

namespace Eden.World.Runtime;

// Registry admission is separate from simulation admission: creating a runtime must
// never require entering another system. Runtime identities remain stable for the
// lifetime of the live world, including while transactions wait for their owners.
internal sealed class SystemRuntimeRegistry<T>(Func<int, T> create)
{
    private readonly ConcurrentDictionary<int, Lazy<Entry>> _systems = new();

    internal sealed class Entry(int id, T state)
    {
        internal int Id { get; } = id;
        internal T State { get; } = state;
        internal OwnershipMonitor Ownership { get; } = new("system");
        private long _revision;
        internal long Revision => Volatile.Read(ref _revision);

        internal IDisposable Enter(string operation)
        {
            bool nested = Ownership.IsEntered;
            var lease = Ownership.Enter(operation, Id);
            return new Lease(this, lease, nested);
        }

        internal IDisposable? TryEnter(string operation)
        {
            bool nested = Ownership.IsEntered;
            return Ownership.TryEnter(operation, Id) is { } lease ? new Lease(this, lease, nested) : null;
        }

        private sealed class Lease(Entry entry, IDisposable ownership, bool nested) : IDisposable
        {
            private IDisposable? _ownership = ownership;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _ownership, null) is { } held)
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

    internal Entry Get(int system) => _systems.GetOrAdd(system, id => new(() => new Entry(id, create(id)))).Value;

    internal IReadOnlyList<Entry> Snapshot() =>
        _systems.Values.Where(value => value.IsValueCreated).Select(value => value.Value).OrderBy(entry => entry.Id).ToArray();

    internal IDisposable Enter(IEnumerable<int> systems, string operation)
    {
        var entries = systems.Distinct().Order().Select(Get).ToArray();
        List<IDisposable> leases = [];
        try
        {
            foreach (var entry in entries)
            {
                leases.Add(entry.Enter(operation));
            }
            return new GroupLease(leases);
        }
        catch
        {
            new GroupLease(leases).Dispose();
            throw;
        }
    }

    internal IDisposable? TryEnter(IEnumerable<int> systems, string operation)
    {
        List<IDisposable> leases = [];
        foreach (var entry in systems.Distinct().Order().Select(Get))
        {
            if (entry.TryEnter(operation) is not { } lease)
            {
                new GroupLease(leases).Dispose();
                return null;
            }
            leases.Add(lease);
        }
        return new GroupLease(leases);
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
