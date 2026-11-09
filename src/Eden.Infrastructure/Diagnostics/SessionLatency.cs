using System.Diagnostics;
using System.Globalization;

namespace Eden.Infrastructure.Diagnostics;

public enum SessionLatencyPhase
{
    WorldWait,
    TrafficRefresh,
    TrafficReconcile,
    PendingDamage,
    Handler,
    Entry,
    Updates,
    Motion,
    Contacts,
    Output,
}

// These inclusive phase times may overlap. In particular, world waits can occur inside updates.
public sealed class SessionLatency : IDisposable
{
    private const int SlowMilliseconds = 250;
    private static readonly SessionLatencyPhase[] s_phases = Enum.GetValues<SessionLatencyPhase>();
    public static string? CurrentDescription => s_current.Value?._description;
    private string? _blockingOwner;
    private double _longestWait;
    private static readonly AsyncLocal<SessionLatency?> s_current = new();
    private readonly SessionLatency? _parent = s_current.Value;
    private readonly long[] _elapsed = new long[s_phases.Length];
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly string _description;
    private readonly Action<string> _warning;
    private readonly Action<string>? _trace;
    private readonly double _queueMilliseconds;
    private double? _replyMilliseconds;

    public SessionLatency(string description, Action<string> warning, Action<string>? trace = null, long? received = null)
    {
        _description = description;
        _warning = warning;
        _trace = trace;
        _queueMilliseconds = received is { } arrival ? Stopwatch.GetElapsedTime(arrival, _started).TotalMilliseconds : 0;
        s_current.Value = this;
    }

    public static Measurement Measure(SessionLatencyPhase phase) => new(s_current.Value, phase);

    public static void ObserveWorldWait(string? owner, long started)
    {
        if (s_current.Value is { } current)
        {
            double wait = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (wait > current._longestWait)
            {
                current._longestWait = wait;
                current._blockingOwner = owner;
            }
        }
    }

    public void ReplyWritten() => _replyMilliseconds ??= Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

    public void Dispose()
    {
        s_current.Value = _parent;
        double total = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        if (total + _queueMilliseconds < SlowMilliseconds && !SessionPerformance.IsActive)
        {
            return;
        }
        string phases = string.Join(", ", s_phases.Select(phase => $"{phase}={_elapsed[(int)phase] * 1000.0 / Stopwatch.Frequency:F2} ms"));
        string reply = _replyMilliseconds is { } elapsed ? $", reply={elapsed:F2} ms" : string.Empty;
        string blocker = _blockingOwner is null ? string.Empty : $", observedOwner={_blockingOwner}";
        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"Session latency {_description}: total={total:F2} ms, queue={_queueMilliseconds:F2} ms{reply}{blocker}, {phases}."
        );
        if (total + _queueMilliseconds >= SlowMilliseconds)
        {
            _warning(message);
        }
        else
        {
            _trace?.Invoke(message);
        }
    }

    public readonly struct Measurement : IDisposable
    {
        private readonly SessionLatency? _owner;
        private readonly SessionLatencyPhase _phase;
        private readonly long _started;

        internal Measurement(SessionLatency? owner, SessionLatencyPhase phase)
        {
            _owner = owner;
            _phase = phase;
            _started = owner is null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_owner is not null)
            {
                _owner._elapsed[(int)_phase] += Stopwatch.GetTimestamp() - _started;
            }
        }
    }
}
