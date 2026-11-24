using System.Diagnostics;

namespace Eden.Infrastructure.Diagnostics;

public sealed record OwnershipTiming(
    string Boundary,
    string Operation,
    int? SystemId,
    TimeSpan Wait,
    TimeSpan Hold,
    string? Blocker,
    string? Request = null,
    long AcquiredAt = 0
)
{
    public long TimestampFrequency => Stopwatch.Frequency;
}

// A reentrant acquisition is part of its outer operation, rather than another wait or hold.
public sealed class OwnershipMonitor(string boundary, object? gate = null)
{
    private readonly string _boundary = boundary;
    private readonly object _gate = gate ?? new();
    private readonly object _admission = new();
    private readonly LinkedList<object> _waiting = new();
    private bool _occupied;
    private string? _operation;
    private int? _system;
    private Action<OwnershipTiming>? _observer;
    private static readonly AsyncLocal<Action<OwnershipTiming>?> s_observer = new();

    public bool IsEntered => Monitor.IsEntered(_gate);

    public int WaitingCount
    {
        get
        {
            lock (_admission)
            {
                return _waiting.Count;
            }
        }
    }

    public static IDisposable Observe(Action<OwnershipTiming> observer)
    {
        var previous = s_observer.Value;
        s_observer.Value = observer;
        return new Observation(previous);
    }

    public IDisposable Subscribe(Action<OwnershipTiming> observer)
    {
        if (Interlocked.CompareExchange(ref _observer, observer, null) is not null)
        {
            throw new InvalidOperationException("This ownership boundary already has a timing subscriber.");
        }
        return new Subscription(this);
    }

    public void AttributeSystem(int system)
    {
        if (!IsEntered)
        {
            throw new InvalidOperationException("System attribution requires ownership.");
        }
        _system = system;
    }

    public IDisposable Enter(string operation, int? systemId = null)
    {
        if (IsEntered)
        {
            var nested = new Lease(this, null, null, null);
            Monitor.Enter(_gate);
            return nested;
        }
        string? blocker = Volatile.Read(ref _operation);
        string description = systemId is { } system ? $"{operation} system={system}" : operation;
        var lease = new Lease(this, operation, systemId, blocker);
        long started = Stopwatch.GetTimestamp();
        using (SessionLatency.Measure(SessionLatencyPhase.WorldWait))
        {
            lock (_admission)
            {
                if (_occupied || _waiting.First is not null)
                {
                    var request = _waiting.AddLast(new object());
                    try
                    {
                        while (_occupied || _waiting.First != request)
                        {
                            Monitor.Wait(_admission);
                        }
                        Monitor.Enter(_gate);
                    }
                    catch
                    {
                        _waiting.Remove(request);
                        Monitor.PulseAll(_admission);
                        throw;
                    }
                    _waiting.Remove(request);
                }
                else
                {
                    Monitor.Enter(_gate);
                }
                _occupied = true;
            }
        }
        SessionLatency.ObserveWorldWait(blocker, started);
        Volatile.Write(ref _operation, description);
        _system = systemId;
        lease.Start(Stopwatch.GetElapsedTime(started));
        return lease;
    }

    public IDisposable? TryEnter(string operation, int? systemId = null)
    {
        if (IsEntered)
        {
            return Enter(operation, systemId);
        }
        Lease lease;
        string description;
        lock (_admission)
        {
            if (_occupied || _waiting.Count != 0)
            {
                return null;
            }
            description = systemId is { } system ? $"{operation} system={system}" : operation;
            lease = new(this, operation, systemId, null);
            Monitor.Enter(_gate);
            _occupied = true;
        }
        Volatile.Write(ref _operation, description);
        _system = systemId;
        lease.Start(TimeSpan.Zero);
        return lease;
    }

    private sealed class Observation(Action<OwnershipTiming>? previous) : IDisposable
    {
        public void Dispose() => s_observer.Value = previous;
    }

    private sealed class Subscription(OwnershipMonitor owner) : IDisposable
    {
        public void Dispose() => Interlocked.Exchange(ref owner._observer, null);
    }

    private sealed class Lease(OwnershipMonitor owner, string? operation, int? system, string? blocker) : IDisposable
    {
        private OwnershipMonitor? _owner = owner;
        private long _started;
        private TimeSpan _wait;

        internal void Start(TimeSpan wait)
        {
            _wait = wait;
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } held)
            {
                return;
            }
            TimeSpan hold = operation is null ? default : Stopwatch.GetElapsedTime(_started);
            int? observedSystem = held._system ?? system;
            if (operation is not null)
            {
                Volatile.Write(ref held._operation, null);
                held._system = null;
            }
            Monitor.Exit(held._gate);
            if (operation is not null)
            {
                lock (held._admission)
                {
                    held._occupied = false;
                    Monitor.PulseAll(held._admission);
                }
                try
                {
                    SessionLatency.ObserveOwnership(_started);
                    (Volatile.Read(ref held._observer) ?? s_observer.Value)?.Invoke(
                        new(held._boundary, operation, observedSystem, _wait, hold, blocker, SessionLatency.CurrentDescription, _started)
                    );
                }
                catch (Exception)
                {
                    // Diagnostics run after release and cannot turn a committed payment into a failed call.
                }
            }
        }
    }
}
