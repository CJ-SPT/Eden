using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Capsuleers;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;

namespace Eden.World.Runtime;

internal sealed record WorldCheckpoint(
    long Generation,
    DateTimeOffset CompletedAt,
    TimeSpan Wait,
    TimeSpan Capture,
    TimeSpan Write,
    long Bytes,
    int? Commands = null,
    int? Connections = null,
    TimeSpan Projection = default,
    TimeSpan Copy = default
);

// The resident relational image preserves the existing transaction, constraint and trigger rules.
// Checkpoints copy the SQL image and capture immutable combat state under the owner.
// Projection and filesystem work use only the detached image outside that owner.
internal sealed class LiveWorld : IDisposable
{
    internal static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<string, LiveWorld> s_worlds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly OwnershipMonitor _ownershipMonitor;
    internal bool IsEntered => _ownershipMonitor.IsEntered;
    internal int WaitingOwners => _ownershipMonitor.WaitingCount;

    internal IDisposable ObserveOwnership(Action<OwnershipTiming> observer) => _ownershipMonitor.Subscribe(observer);

    private readonly object _scheduleGate = new();
    private readonly Queue<WorldCheckpoint> _checkpoints = new();
    private const int RetainedCheckpointReports = 32;
    private readonly string _path;
    private readonly SqliteConnection _memory;
    private readonly FileStream _ownership;
    private readonly TimeProvider _clock;
    private readonly Action<SqliteConnection, string> _persist;
    private readonly Action<string> _report;
    private readonly Action<string> _reportFailure;
    private readonly Action<string> _reportTrace;
    private readonly ITimer _timer;
    private Task<Exception?> _pending = Task.FromResult<Exception?>(null);
    private long _observedDataVersion;
    private long _generation;
    private long _savedGeneration;
    private bool _stopping;
    private bool _disposed;
    private WorldCheckpoint? _lastCheckpoint;
    private Exception? _lastSaveFailure;
    private long _activityCheckpointTicks;

    internal string ConnectionString { get; }
    internal LiveCombatRuntime? Combat { get; }
    internal LiveReadModels? Reads { get; }
    internal LiveShipHeat Heat { get; }
    internal LiveNaniteRepairs Nanites { get; }
    internal LiveTractors Tractors { get; }
    internal Eden.World.Capsuleers.CapsuleerActivity CapsuleerActivity { get; }
    internal bool MeasureCheckpoints { get; set; }
    internal int SqlConnections { get; private set; }
    private readonly LiveTimedModules? _timedModules;
    internal bool TracksTimedModules => _timedModules is not null;
    private bool _expiringTimedModules;

    internal void SqlOpened() => SqlConnections++;

    internal void SqlClosed(IReadOnlyCollection<CombatRowChange> changes)
    {
        SqlConnections--;
        foreach (var change in changes)
        {
            if (change.Table == "WorldPilot" && CapsuleerJobs.ContainsKey(change.Id))
            {
                CapsuleerHullRevisions[change.Id] = CapsuleerHullRevisions.GetValueOrDefault(change.Id) + 1;
            }
        }
        if (
            changes.Any(c =>
                c.Table is "WorldCapsuleerAgent" or "WorldCapsuleerSystem" or "WorldCapsuleerPopulation" or "WorldCapsuleerProvisioning"
            )
        )
        {
            CapsuleerRevision++;
        }
        _timedModules?.Observe(changes);
        Heat.Observe(changes);
        Nanites.Observe(changes);
        Tractors.Observe(changes);
        Reads?.Observe(changes);
        Combat?.Observe(changes);
        if (changes.Count != 0)
        {
            CapsuleerActivity.Wake();
        }
    }

    internal long CapsuleerRevision { get; private set; }
    internal Dictionary<long, long> CapsuleerHullRevisions { get; } = [];
    internal Dictionary<long, CapsuleerJob> CapsuleerJobs { get; } = [];

    // These versions describe the resident SQL image, which is the source for every backup.
    // Successful file saves cannot clear differences that have not reached that image.
    internal Dictionary<long, CapsuleerJob> ProjectedCapsuleerJobs { get; } = [];
    internal Dictionary<long, MinerCheckpoint> ProjectedMiners { get; } = [];
    internal Dictionary<long, HaulerCheckpoint> ProjectedHaulers { get; } = [];
    internal Dictionary<long, MinerCheckpoint> MinerCheckpoints { get; } = [];
    internal Dictionary<long, HaulerCheckpoint> HaulerCheckpoints { get; } = [];
    internal MinerCheckpoint? MinerCheckpoint => MinerCheckpoints.Values.FirstOrDefault();
    internal HaulerCheckpoint? HaulerCheckpoint => HaulerCheckpoints.Values.FirstOrDefault();

    internal void ObjectsChanged() => _generation++;

    internal static void CatalogChanged(string path)
    {
        foreach (
            var world in s_worlds.Values.Where(w =>
                string.Equals(
                    Path.Combine(Path.GetDirectoryName(w._path)!, "catalog.sqlite"),
                    Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            using var ownership = world.Enter();
            world.Combat?.Clear();
            world.Reads?.Clear();
            world.Heat.InvalidateProfiles();
        }
    }

    internal WorldCheckpoint? LastCheckpoint => Volatile.Read(ref _lastCheckpoint);
    internal Exception? LastSaveFailure => Volatile.Read(ref _lastSaveFailure);
    internal IReadOnlyList<WorldCheckpoint> RecentCheckpoints
    {
        get
        {
            using (Acquire())
            {
                return _checkpoints.ToArray();
            }
        }
    }
    internal Task PendingSave
    {
        get
        {
            lock (_scheduleGate)
            {
                return _pending;
            }
        }
    }

    internal LiveWorld(
        WorldDatabase database,
        TimeProvider clock,
        Action<string>? report = null,
        Action<SqliteConnection, string>? persist = null,
        bool combatObjects = true,
        bool residentReads = true,
        bool simulationObjects = true
    )
    {
        _ownershipMonitor = new("world", _gate);
        _path = Path.GetFullPath(database.Path);
        Heat = new(this, database, clock);
        Tractors = new(this, database);
        Nanites = new(database, clock);
        CapsuleerActivity = new(database, clock);
        _clock = clock;
        _report = report ?? ServerLog.Console.Debug;
        _reportFailure = report ?? ServerLog.Console.Error;
        _reportTrace = report ?? ServerLog.Console.Trace;
        _persist = persist ?? PersistSnapshot;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _ownership = new(_path + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = "eden-world-" + Guid.NewGuid().ToString("N"),
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        _memory = new(ConnectionString);
        try
        {
            _memory.Open();
            using (var disk = WorldConnections.FileConnection(_path))
            {
                disk.Open();
                // Finish any previous WAL before installing standalone checkpoint files.
                using var command = disk.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE";
                if (
                    !string.Equals(
                        Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture),
                        "delete",
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new IOException("The saved world could not leave WAL mode. Stop other users of this world before opening it.");
                }
                disk.BackupDatabase(_memory);
            }
            _observedDataVersion = DataVersion();
            if (!s_worlds.TryAdd(_path, this))
            {
                throw new InvalidOperationException("This world already has a resident owner.");
            }
            Combat = combatObjects ? new(this, database, simulationObjects) : null;
            Reads = residentReads ? new(this, database) : null;
            _timedModules = new(database);
            Combat?.WarmDrones();
            Heat.Warm();
            using (Enter())
            {
                Nanites.Advance(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            }
            var interval = TimeSpan.FromSeconds(database.Settings.Simulation.CheckpointSeconds);
            _timer = clock.CreateTimer(_ => QueueSave(), null, interval, interval);
        }
        catch
        {
            if (Find(_path) == this)
            {
                s_worlds.TryRemove(_path, out _);
            }
            _memory.Dispose();
            _ownership.Dispose();
            throw;
        }
    }

    internal static LiveWorld? Find(string path) =>
        s_worlds.TryGetValue(path, out var world) ? world : s_worlds.GetValueOrDefault(Path.GetFullPath(path));

    internal IDisposable Enter([CallerMemberName] string operation = "", int? systemId = null)
    {
        bool nested = Monitor.IsEntered(_gate);
        var lease = Acquire(operation, systemId);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A final checkpoint already owns the gate and must project dirty objects.
            // New external work is refused while admitted operations can finish nesting.
            if (Volatile.Read(ref _stopping) && !nested)
            {
                throw new InvalidOperationException("The resident world is shutting down.");
            }
            if (SqlConnections == 0 && !_expiringTimedModules)
            {
                _expiringTimedModules = true;
                try
                {
                    using var timing = SessionLatency.Measure(SessionLatencyPhase.TimedEffects);
                    _timedModules?.Expire(_clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
                    Heat.Advance(_clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
                    Nanites.Advance(_clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
                }
                finally
                {
                    _expiringTimedModules = false;
                }
            }
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private const int SlowOwnershipMilliseconds = 250;
    private string? _ownerDescription;
    private CapsuleerJob? _trafficJob;
    private int _trafficSystem;

    internal void ObserveTrafficJob(CapsuleerJob job, int system)
    {
        _ownershipMonitor.AttributeSystem(system);
        _trafficJob = job;
        _trafficSystem = system;
    }

    private IDisposable Acquire([CallerMemberName] string operation = "", int? systemId = null)
    {
        bool nested = Monitor.IsEntered(_gate);
        var ownership = _ownershipMonitor.Enter(operation, systemId);
        if (!nested)
        {
            Volatile.Write(ref _ownerDescription, SessionLatency.CurrentDescription ?? operation);
        }
        return new Lease(this, ownership, nested, Stopwatch.GetTimestamp());
    }

    private sealed class Lease(LiveWorld world, IDisposable ownership, bool nested, long started) : IDisposable
    {
        private LiveWorld? _world = world;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _world, null) is { } held)
            {
                string? operation = held._ownerDescription;
                CapsuleerJob? job = null;
                int system = 0;
                if (!nested)
                {
                    job = held._trafficJob;
                    system = held._trafficSystem;
                    held._trafficJob = null;
                    Volatile.Write(ref held._ownerDescription, null);
                }
                var elapsed = nested ? TimeSpan.Zero : Stopwatch.GetElapsedTime(started);
                ownership.Dispose();
                if (!nested && elapsed.TotalMilliseconds >= SlowOwnershipMilliseconds)
                {
                    string detail = job is null ? "" : $" Included {job.Action} for pilot {job.PilotId} in system {system}.";
                    ServerLog.Console.Warning($"Slow world owner {operation}: {elapsed.TotalMilliseconds:F2} ms held.{detail}");
                }
            }
        }
    }

    private long DataVersion()
    {
        using var command = _memory.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private long ObserveGeneration()
    {
        long version = DataVersion();
        if (version != _observedDataVersion)
        {
            _observedDataVersion = version;
            _generation++;
        }
        return _generation;
    }

    private Task<Exception?> QueueSave(bool duringShutdown = false)
    {
        lock (_scheduleGate)
        {
            if (_disposed || _stopping && !duringShutdown || !_pending.IsCompleted)
            {
                return _pending;
            }
            // Timers and save requests must not inherit a session's diagnostic or operation scope.
            using (ExecutionContext.SuppressFlow())
            {
                _pending = Task.Run(Save);
            }
            return _pending;
        }
    }

    internal async Task FlushAsync()
    {
        if (Monitor.IsEntered(_gate))
        {
            throw new InvalidOperationException("Complete world connections before awaiting a durable checkpoint.");
        }
        long target;
        using (Acquire())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            target = ObserveGeneration();
        }
        while (true)
        {
            if (await QueueSave(duringShutdown: true) is { } failure)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            using (Acquire())
            {
                if (_savedGeneration >= target)
                {
                    return;
                }
            }
        }
    }

    internal WorldSnapshot? CaptureSnapshot()
    {
        long started = Stopwatch.GetTimestamp();
        using (Acquire("checkpoint capture"))
        {
            var wait = Stopwatch.GetElapsedTime(started);
            started = Stopwatch.GetTimestamp();
            if (_disposed)
            {
                return null;
            }
            if (SqlConnections != 0)
            {
                throw new InvalidOperationException("Complete world connections before capturing a checkpoint.");
            }
            Heat.Advance(_clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            Nanites.Advance(_clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            var activity = CapsuleerActivity.Capture();
            if (activity is not null && activity.ElapsedTicks != _activityCheckpointTicks)
            {
                _activityCheckpointTicks = activity.ElapsedTicks;
                ObjectsChanged();
            }
            long generation = ObserveGeneration();
            if (generation == _savedGeneration)
            {
                return null;
            }
            var combat = Combat?.CaptureCheckpoint();
            SqliteConnection database = new("Data Source=:memory:;Pooling=False;Foreign Keys=True");
            try
            {
                database.Open();
                long copyStarted = Stopwatch.GetTimestamp();
                _memory.BackupDatabase(database);
                TimeSpan copy = Stopwatch.GetElapsedTime(copyStarted);
                return new(
                    database,
                    combat,
                    generation,
                    wait,
                    Stopwatch.GetElapsedTime(started),
                    Heat.Capture(),
                    Tractors.Capture(),
                    MinerCheckpoints.Values.Where(m => ProjectedMiners.GetValueOrDefault(m.PilotId) != m).ToArray(),
                    HaulerCheckpoints.Values.Where(h => ProjectedHaulers.GetValueOrDefault(h.PilotId) != h).ToArray(),
                    activity,
                    new CapsuleerJobCheckpoint(
                        CapsuleerJobs.Values.Where(j => ProjectedCapsuleerJobs.GetValueOrDefault(j.PilotId) != j).ToArray()
                    ),
                    copy
                );
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }
    }

    private Exception? Save()
    {
        using var metrics = SessionPerformance.Begin("checkpoint", message => Report(message, trace: true), force: MeasureCheckpoints);
        try
        {
            using var snapshot = CaptureSnapshot();
            if (snapshot is null)
            {
                return null;
            }
            var projection = snapshot.Project();
            long started = Stopwatch.GetTimestamp();
            _persist(snapshot.Database, _path);
            TimeSpan write = Stopwatch.GetElapsedTime(started);
            WorldCheckpoint completed = new(
                snapshot.Generation,
                _clock.GetUtcNow(),
                snapshot.Wait,
                snapshot.Capture,
                write,
                new FileInfo(_path).Length,
                metrics?.Commands,
                metrics?.Connections,
                projection,
                snapshot.Copy
            );
            using (Acquire())
            {
                _savedGeneration = snapshot.Generation;
                Volatile.Write(ref _lastCheckpoint, completed);
                Volatile.Write(ref _lastSaveFailure, null);
                _checkpoints.Enqueue(completed);
                while (_checkpoints.Count > RetainedCheckpointReports)
                {
                    _checkpoints.Dequeue();
                }
            }
            Report(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"World checkpoint {completed.Generation}: wait={completed.Wait.TotalMilliseconds:F2} ms, "
                        + $"capture={completed.Capture.TotalMilliseconds:F2} ms, copy={completed.Copy.TotalMilliseconds:F2} ms, "
                        + $"project={projection.TotalMilliseconds:F2} ms, "
                        + $"write={write.TotalMilliseconds:F2} ms, bytes={completed.Bytes}."
                )
            );
            return null;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _lastSaveFailure, exception);
            Report($"World checkpoint failed; resident changes remain pending: {exception.Message}", failure: true);
            return exception;
        }
    }

    internal static void PersistSnapshot(SqliteConnection snapshot, string path)
    {
        string staging = path + ".save-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var disk = WorldConnections.FileConnection(staging))
            {
                disk.Open();
                snapshot.BackupDatabase(disk);
            }
            using (FileStream file = new(staging, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                File.Replace(staging, path, path + ".previous");
            }
            else
            {
                File.Move(staging, path);
            }
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private void Report(string message, bool failure = false, bool trace = false)
    {
        try
        {
            (
                failure ? _reportFailure
                : trace ? _reportTrace
                : _report
            )(message);
        }
        catch (Exception)
        {
            // A closed log sink must not turn a completed checkpoint into a failed save.
        }
    }

    public void Dispose()
    {
        if (Monitor.IsEntered(_gate))
        {
            throw new InvalidOperationException("Complete world connections before shutting down the resident world.");
        }
        lock (_scheduleGate)
        {
            if (_disposed)
            {
                return;
            }
            _stopping = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        try
        {
            FlushAsync().GetAwaiter().GetResult();
        }
        catch
        {
            lock (_scheduleGate)
            {
                _stopping = false;
                _timer.Change(SaveInterval, SaveInterval);
            }
            throw;
        }
        using (Acquire())
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            s_worlds.TryRemove(_path, out _);
            _memory.Dispose();
            _ownership.Dispose();
        }
        _timer.Dispose();
    }
}
