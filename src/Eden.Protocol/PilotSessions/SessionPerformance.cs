using System.Diagnostics;
using System.Globalization;

namespace Eden.Protocol.PilotSessions;

public sealed class SessionPerformance : IDisposable
{
    private static readonly AsyncLocal<SessionPerformance?> s_current = new();
    private static readonly bool s_enabled = Environment.GetEnvironmentVariable("EDEN_TRACE_PERFORMANCE") == "1";
    private readonly SessionPerformance? _parent = s_current.Value;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly string _name;
    private readonly Action<string> _report;
    public int Commands { get; private set; }
    public int Connections { get; private set; }
    public int TackleReads { get; private set; }
    public int AggressionReads { get; private set; }
    public static bool IsActive => s_current.Value is not null;

    private SessionPerformance(string name, Action<string> report)
    {
        _name = name;
        _report = report;
        s_current.Value = this;
    }

    public static SessionPerformance? Begin(string name, Action<string> report, bool force = false) =>
        force || s_enabled || IsActive ? new(name, report) : null;

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

    public static void DatabaseCommand(string sql)
    {
        for (var scope = s_current.Value; scope is not null; scope = scope._parent)
        {
            scope.Commands++;
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
        _report(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Performance {_name}: {Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F2} ms, "
                    + $"commands={Commands}, connections={Connections}, tackleReads={TackleReads}, aggressionReads={AggressionReads}."
            )
        );
    }
}
