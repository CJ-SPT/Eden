using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;

namespace Eden.Server.Persistence;

internal sealed record WorldCheckpoint(
    long Generation,
    DateTimeOffset CompletedAt,
    TimeSpan Wait,
    TimeSpan Capture,
    TimeSpan Write,
    long Bytes
);

// The resident relational image preserves the existing transaction, constraint and trigger rules.
// Checkpoints copy it to a second RAM image before doing any filesystem work.
internal sealed class LiveWorld : IDisposable
{
    internal static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<string, LiveWorld> s_worlds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly object _scheduleGate = new();
    private readonly Queue<WorldCheckpoint> _checkpoints = new();
    private const int RetainedCheckpointReports = 32;
    private readonly string _path;
    private readonly SqliteConnection _memory;
    private readonly FileStream _ownership;
    private readonly TimeProvider _clock;
    private readonly Action<SqliteConnection, string> _persist;
    private readonly Action<string> _report;
    private readonly ITimer _timer;
    private Task<Exception?> _pending = Task.FromResult<Exception?>(null);
    private long _observedDataVersion;
    private long _generation;
    private long _savedGeneration;
    private bool _stopping;
    private bool _disposed;
    private WorldCheckpoint? _lastCheckpoint;
    private Exception? _lastSaveFailure;

    internal string ConnectionString { get; }
    internal WorldCheckpoint? LastCheckpoint => Volatile.Read(ref _lastCheckpoint);
    internal Exception? LastSaveFailure => Volatile.Read(ref _lastSaveFailure);
    internal IReadOnlyList<WorldCheckpoint> RecentCheckpoints
    {
        get
        {
            lock (_gate)
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
        Action<SqliteConnection, string>? persist = null
    )
    {
        _path = Path.GetFullPath(database.Path);
        _clock = clock;
        _report = report ?? Console.WriteLine;
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
            _timer = clock.CreateTimer(_ => QueueSave(), null, SaveInterval, SaveInterval);
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

    internal static LiveWorld? Find(string path) => s_worlds.GetValueOrDefault(Path.GetFullPath(path));

    internal IDisposable Enter()
    {
        Monitor.Enter(_gate);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Volatile.Read(ref _stopping))
            {
                throw new InvalidOperationException("The resident world is shutting down.");
            }
            return new Lease(_gate);
        }
        catch
        {
            Monitor.Exit(_gate);
            throw;
        }
    }

    private sealed class Lease(object gate) : IDisposable
    {
        private object? _gate = gate;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _gate, null) is { } held)
            {
                Monitor.Exit(held);
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
        lock (_gate)
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
            lock (_gate)
            {
                if (_savedGeneration >= target)
                {
                    return;
                }
            }
        }
    }

    private Exception? Save()
    {
        SqliteConnection? snapshot = null;
        try
        {
            long started = Stopwatch.GetTimestamp();
            long generation;
            TimeSpan wait;
            TimeSpan capture;
            lock (_gate)
            {
                wait = Stopwatch.GetElapsedTime(started);
                started = Stopwatch.GetTimestamp();
                if (_disposed || (generation = ObserveGeneration()) == _savedGeneration)
                {
                    return null;
                }
                snapshot = new("Data Source=:memory:;Pooling=False");
                snapshot.Open();
                _memory.BackupDatabase(snapshot);
                capture = Stopwatch.GetElapsedTime(started);
            }
            started = Stopwatch.GetTimestamp();
            _persist(snapshot, _path);
            TimeSpan write = Stopwatch.GetElapsedTime(started);
            WorldCheckpoint completed = new(generation, _clock.GetUtcNow(), wait, capture, write, new FileInfo(_path).Length);
            lock (_gate)
            {
                _savedGeneration = generation;
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
                    $"World checkpoint {generation}: wait={wait.TotalMilliseconds:F2} ms, capture={capture.TotalMilliseconds:F2} ms, "
                        + $"write={write.TotalMilliseconds:F2} ms, bytes={completed.Bytes}."
                )
            );
            return null;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _lastSaveFailure, exception);
            Report($"World checkpoint failed; resident changes remain pending: {exception.Message}");
            return exception;
        }
        finally
        {
            snapshot?.Dispose();
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

    private void Report(string message)
    {
        try
        {
            _report(message);
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
        lock (_gate)
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
