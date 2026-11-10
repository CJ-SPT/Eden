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
    TimeSpan Copy = default,
    long CaptureAllocatedBytes = 0,
    long CopyAllocatedBytes = 0,
    long ProjectionAllocatedBytes = 0,
    long WriteAllocatedBytes = 0,
    long CopyBytes = 0
);

// The resident relational image preserves the existing transaction, constraint and trigger rules.
// Checkpoints capture immutable state under system owners, then copy the SQL image
// under persistence alone. Projection and file writing use the detached image.
internal sealed partial class LiveWorld : IDisposable
{
    internal static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<string, LiveWorld> s_worlds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly OwnershipMonitor _ownershipMonitor;
    internal bool IsEntered => _ownershipMonitor.IsEntered || _heldSystems.Value!.Count != 0;
    internal int WaitingOwners => _ownershipMonitor.WaitingCount;

    internal IDisposable ObserveOwnership(Action<OwnershipTiming> observer) => _ownershipMonitor.Subscribe(observer);

    private readonly object _scheduleGate = new();
    private readonly Queue<WorldCheckpoint> _checkpoints = new();
    private const int RetainedCheckpointReports = 32;
    private readonly string _path;
    private readonly WorldDatabase _database;
    private readonly SqliteConnection _memory;
    private readonly FileStream _ownership;
    private readonly TimeProvider _clock;
    private readonly Action<SqliteConnection, string> _persist;
    private readonly Action<SqliteConnection, SqliteConnection> _copy;
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
    internal LiveCombatRuntime? Combat => Current.Combat;
    internal LiveReadModels? Reads => Current.Reads;
    internal LiveShipHeat Heat => Current.Heat;
    internal LiveNaniteRepairs Nanites => Current.Nanites;
    internal LiveTractors Tractors => Current.Tractors;
    internal Eden.World.Capsuleers.CapsuleerActivity CapsuleerActivity { get; }
    internal bool MeasureCheckpoints { get; set; }
    private readonly ThreadLocal<int> _sqlConnections = new();
    internal int SqlConnections => _sqlConnections.Value;
    private LiveTimedModules _timedModules => Current.TimedModules;
    internal bool TracksTimedModules => true;

    internal void SqlOpened() => _sqlConnections.Value++;

    internal void SqlClosed(
        IReadOnlyCollection<CombatRowChange> changes,
        TransactionParticipants? participants = null,
        int? projectionSystem = null,
        IReadOnlySet<int>? committedSystems = null
    )
    {
        _sqlConnections.Value--;
        if (changes.Count == 0)
        {
            return;
        }
        IReadOnlyCollection<int>? affectedSystems = committedSystems;
        if (affectedSystems is null && projectionSystem is int source)
        {
            affectedSystems = [source];
        }
        else if (
            affectedSystems is null
            && participants is not null
            && participants.Systems.Count + participants.Pilots.Count + participants.Ships.Count + participants.Resources.Count != 0
        )
        {
            affectedSystems = ParticipantSystems(participants);
        }
        ReconcileHeatMembership(changes);
        if (changes.Any(change => change.Table is "WorldPilot" or "WorldTrafficIdentity" or "schema"))
        {
            PopulationIdentityRevision++;
        }
        foreach (var change in changes)
        {
            if (change.Table == "WorldPilot" && CapsuleerJobs.ContainsKey(change.Id))
            {
                CapsuleerHullRevisions[change.Id] = CapsuleerHullRevisions.GetValueOrDefault(change.Id) + 1;
            }
        }
        if (
            changes.Any(c =>
                c.Table
                    is "WorldCapsuleerAgent"
                        or "WorldCapsuleerSystem"
                        or "WorldCapsuleerPopulation"
                        or "WorldCapsuleerProvisioning"
                        or "WorldCapsuleerSuspension"
                        or "schema"
            )
        )
        {
            CapsuleerRevision++;
            foreach (var change in changes.Where(c => c.Table is "WorldCapsuleerSystem" or "WorldCapsuleerPopulation"))
            {
                CapsuleerSystemRevisions[checked((int)change.Id)] = CapsuleerRevision;
            }
            if (changes.Any(c => c.Table is "WorldCapsuleerProvisioning" or "schema"))
            {
                CapsuleerMetadataReset = CapsuleerRevision;
            }
        }
        foreach (var change in changes)
        {
            if (change.Table == "schema")
            {
                _pilotSystems.Clear();
                _shipSystems.Clear();
                _npcSystems.Clear();
                _shipOwners.Clear();
            }
            else if (change.Table == "WorldPilot")
            {
                _pilotSystems.TryRemove(change.Id, out _);
            }
            else if (change.Table is "WorldPositionMembership" or "WorldShip")
            {
                _shipSystems.TryRemove(change.Id, out _);
                if (_shipOwners.TryGetValue(change.Id, out long pilot))
                {
                    _pilotSystems.TryRemove(pilot, out _);
                }
            }
        }
        using (UseShared())
        {
            ObserveState(_shared, changes);
        }
        var committed = changes.ToArray();
        foreach (var entry in _systems.Snapshot())
        {
            if (affectedSystems is not null && !affectedSystems.Contains(entry.Id))
            {
                continue;
            }
            if (entry.Ownership.IsEntered)
            {
                using var context = UseSystem(entry.Id);
                ObserveState(entry.State, committed);
            }
            else
            {
                entry.State.PendingChanges.Enqueue(committed);
            }
        }
        if (changes.Count != 0)
        {
            CapsuleerActivity.Wake();
        }
    }

    private static void ObserveState(LiveSystemState state, IReadOnlyCollection<CombatRowChange> changes)
    {
        state.TimedModules.Observe(changes);
        state.Heat.Observe(changes);
        state.Nanites.Observe(changes);
        state.Tractors.Observe(changes);
        state.Reads?.Observe(changes);
        state.Combat?.Observe(changes);
    }

    internal long CapsuleerRevision { get; private set; }
    internal long CapsuleerMetadataReset { get; private set; }
    internal Dictionary<int, long> CapsuleerSystemRevisions { get; } = [];
    internal long PopulationIdentityRevision { get; private set; }
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

    internal void ObjectsChanged() => Interlocked.Increment(ref _generation);

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
            using var ownership = world.EnterCoordinated("catalog change");
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
        bool simulationObjects = true,
        Action<SqliteConnection, SqliteConnection>? copy = null
    )
    {
        _database = database;
        _ownershipMonitor = new("world", _gate);
        _path = Path.GetFullPath(database.Path);
        _shared = new(this, database, clock, combatObjects, residentReads, simulationObjects);
        _systems = new(
            system => new(this, database, clock, combatObjects, residentReads, simulationObjects, system),
            () => _heldSystems.Value!.Count == 0 || _ownershipMonitor.IsEntered
        );
        CapsuleerActivity = new(database, clock);
        _clock = clock;
        _report = report ?? ServerLog.Console.Debug;
        _reportFailure = report ?? ServerLog.Console.Error;
        _reportTrace = report ?? ServerLog.Console.Trace;
        _persist = persist ?? PersistSnapshot;
        _copy = copy ?? ((source, destination) => source.BackupDatabase(destination));
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
        if (systemId is int specified && CurrentSystem != specified)
        {
            var context = UseSystem(specified);
            try
            {
                return new CoordinatedLease(context, EnterSimulation(operation, specified));
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }
        if (CurrentSystem is int system)
        {
            return EnterSimulation(operation, system);
        }
        bool nested = _ownershipMonitor.IsEntered;
        var lease = EnterPersistence(operation);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping && !nested)
            {
                throw new InvalidOperationException("The resident world is shutting down.");
            }
            AdvanceEffects(_shared);
            if (!nested)
            {
                AdvanceSystemHeat();
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
            Interlocked.Increment(ref _generation);
        }
        return Interlocked.Read(ref _generation);
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
        if (IsEntered)
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
        using var context = UseShared();
        long started = Stopwatch.GetTimestamp();
        using (EnterPersistence("checkpoint capture"))
        {
            var wait = Stopwatch.GetElapsedTime(started);
            if (_disposed)
            {
                return null;
            }
            if (SqlConnections != 0)
            {
                throw new InvalidOperationException("Complete world connections before capturing a checkpoint.");
            }
            var resident = CaptureResidents();
            if (resident is null)
            {
                return null;
            }
            // System owners and registry admission are released. Persistence remains
            // held so SQL cannot advance beyond the captured resident generation.
            SqliteConnection database = new("Data Source=:memory:;Pooling=False;Foreign Keys=True");
            try
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long copyStarted = Stopwatch.GetTimestamp();
                database.Open();
                Volatile.Write(ref _checkpointCopying, true);
                try
                {
                    _copy(_memory, database);
                }
                finally
                {
                    Volatile.Write(ref _checkpointCopying, false);
                }
                TimeSpan copy = Stopwatch.GetElapsedTime(copyStarted);
                long copyAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                using var size = database.CreateCommand();
                size.CommandText = "SELECT page_count*page_size FROM pragma_page_count(),pragma_page_size()";
                long bytes = Convert.ToInt64(size.ExecuteScalar(), CultureInfo.InvariantCulture);
                return new(
                    database,
                    resident.Combat,
                    resident.Generation,
                    wait + resident.Wait,
                    resident.Capture,
                    resident.Heat,
                    resident.Tractors,
                    resident.Miners,
                    resident.Haulers,
                    resident.Activity,
                    resident.Jobs,
                    copy,
                    resident.AllocatedBytes,
                    copyAllocated,
                    bytes
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
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            _persist(snapshot.Database, _path);
            TimeSpan write = Stopwatch.GetElapsedTime(started);
            long writeAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
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
                snapshot.Copy,
                snapshot.CaptureAllocatedBytes,
                snapshot.CopyAllocatedBytes,
                snapshot.ProjectionAllocatedBytes,
                writeAllocated,
                snapshot.CopyBytes
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
                        + $"write={write.TotalMilliseconds:F2} ms, bytes={completed.Bytes}, copyBytes={completed.CopyBytes}, "
                        + $"allocated capture/copy/project/write={completed.CaptureAllocatedBytes}/{completed.CopyAllocatedBytes}/"
                        + $"{completed.ProjectionAllocatedBytes}/{completed.WriteAllocatedBytes}."
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
        if (IsEntered)
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
