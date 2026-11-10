using System.Diagnostics;
using System.Globalization;

namespace Eden.Infrastructure.Diagnostics;

public readonly record struct PerformanceSample(
    string Name,
    TimeSpan Elapsed,
    int Commands,
    int Connections,
    int Fittings,
    double SqlMilliseconds,
    double CommitMilliseconds,
    double BeginMilliseconds,
    long AllocatedBytes
);

public sealed class SessionPerformance : IDisposable
{
    private static readonly AsyncLocal<SessionPerformance?> s_current = new();
    private static readonly bool s_enabled = Environment.GetEnvironmentVariable("EDEN_TRACE_PERFORMANCE") == "1";
    private readonly SessionPerformance? _parent = s_current.Value;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly string _name;
    private readonly Action<string>? _report;
    private readonly Action<string>? _command;
    private readonly Action<PerformanceSample>? _sample;
    private readonly long _allocated = GC.GetAllocatedBytesForCurrentThread();
    public int FittingCalculations { get; private set; }
    public int Commands { get; private set; }
    public int Connections { get; private set; }
    public int TackleReads { get; private set; }
    public int AggressionReads { get; private set; }
    public static bool IsActive => s_current.Value is not null;

    private SessionPerformance(
        string name,
        Action<string>? report,
        Action<PerformanceSample>? sample = null,
        Action<string>? command = null
    )
    {
        _name = name;
        _command = command;
        _report = report;
        _sample = sample ?? _parent?._sample;
        s_current.Value = this;
    }

    public static SessionPerformance? Begin(string name, Action<string> report, bool force = false, Action<string>? command = null) =>
        force || s_enabled || IsActive ? new(name, report, command: command) : null;

    public static SessionPerformance? Phase(string name) => s_current.Value is { } parent ? new(name, parent._report) : null;

    public static SessionPerformance Capture(string name, Action<PerformanceSample> sample) => new(name, s_current.Value?._report, sample);

    public double SqlMilliseconds { get; private set; }
    public double CommitMilliseconds { get; private set; }
    public double TransactionStartMilliseconds { get; private set; }

    public static void TransactionStarted(TimeSpan duration)
    {
        for (var scope = s_current.Value; scope is not null; scope = scope._parent)
        {
            scope.TransactionStartMilliseconds += duration.TotalMilliseconds;
        }
    }

    public static void DatabaseElapsed(TimeSpan duration, bool commit = false)
    {
        for (var scope = s_current.Value; scope is not null; scope = scope._parent)
        {
            if (commit)
            {
                scope.CommitMilliseconds += duration.TotalMilliseconds;
            }
            else
            {
                scope.SqlMilliseconds += duration.TotalMilliseconds;
            }
        }
    }

    public static void ReportQueue(long received, Action<string> report)
    {
        if (s_enabled || IsActive)
        {
            report(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Performance queue: {Stopwatch.GetElapsedTime(received).TotalMilliseconds:F2} ms."
                )
            );
        }
    }

    public static void FittingCalculated()
    {
        for (var scope = s_current.Value; scope is not null; scope = scope._parent)
        {
            scope.FittingCalculations++;
        }
    }

    public static void DatabaseCommand(string sql)
    {
        for (var scope = s_current.Value; scope is not null; scope = scope._parent)
        {
            scope.Commands++;
            scope._command?.Invoke(sql);
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                scope.TackleReads += sql.Contains("WorldTackle", StringComparison.Ordinal) ? 1 : 0;
                scope.AggressionReads += sql.Contains("WorldNpcAggression", StringComparison.Ordinal) ? 1 : 0;
            }
        }
    }

    public static void ConnectionOpened()
    {
        for (var scope = s_current.Value; scope is not null; scope = scope._parent)
        {
            scope.Connections++;
        }
    }

    public void Dispose()
    {
        s_current.Value = _parent;
        var elapsed = Stopwatch.GetElapsedTime(_started);
        _sample?.Invoke(
            new(
                _name,
                elapsed,
                Commands,
                Connections,
                FittingCalculations,
                SqlMilliseconds,
                CommitMilliseconds,
                TransactionStartMilliseconds,
                Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - _allocated)
            )
        );
        _report?.Invoke(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Performance {_name}: {Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F2} ms, "
                    + $"commands={Commands}, connections={Connections}, tackleReads={TackleReads}, "
                    + $"aggressionReads={AggressionReads}, fittings={FittingCalculations}, "
                    + $"sql={SqlMilliseconds:F2} ms, commit={CommitMilliseconds:F2} ms, begin={TransactionStartMilliseconds:F2} ms."
            )
        );
    }
}
