using System.Diagnostics;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    private readonly bool _diskCheckpointCapture;

    private WorldSnapshot? CaptureIncrementalSnapshot()
    {
        using var context = UseShared();
        SqliteConnection? database = null;
        string? captureFile = null;
        WorldDatabaseCopy.Incremental? backup = null;
        TimeSpan wait = TimeSpan.Zero;
        TimeSpan copy = TimeSpan.Zero;
        TimeSpan capture = TimeSpan.Zero;
        TimeSpan maximumPause = TimeSpan.Zero;
        long captureAllocated = 0;
        long copyAllocated = 0;
        int steps = 0;
        bool transferred = false;
        try
        {
            if (_diskCheckpointCapture)
            {
                captureFile = CheckpointCaptureFile.Create(_path);
            }
            database = captureFile is null
                ? new("Data Source=:memory:;Pooling=False;Foreign Keys=True")
                : WorldConnections.FileConnection(captureFile, SqliteOpenMode.ReadWrite);
            database.Open();
            if (captureFile is not null)
            {
                // This image can be regenerated after a crash. The completed file
                // is synced before it replaces the saved world.
                using var configure = database.CreateCommand();
                configure.CommandText = "PRAGMA synchronous=OFF";
                SessionPerformance.CheckpointMetadataCommand(configure.CommandText);
                configure.ExecuteNonQuery();
            }
            WorldResidentCapture completed;
            while (true)
            {
                long requested = Stopwatch.GetTimestamp();
                using (EnterPersistence("checkpoint page batch"))
                {
                    wait += Stopwatch.GetElapsedTime(requested);
                    long pauseStarted = Stopwatch.GetTimestamp();
                    if (_disposed)
                    {
                        return null;
                    }
                    if (SqlConnections != 0)
                    {
                        throw new InvalidOperationException("Complete world connections before capturing a checkpoint.");
                    }
                    if (backup is null)
                    {
                        backup = new(_memory, database);
                        Volatile.Write(ref _checkpointCopying, true);
                        Interlocked.Increment(ref _checkpointCopies);
                    }
                    WorldResidentCapture? resident = null;
                    if (backup.Remaining <= WorldDatabaseCopy.PagesPerStep)
                    {
                        resident = CaptureResidents();
                        if (resident is null)
                        {
                            return null;
                        }
                        wait += resident.Wait;
                        capture += resident.Capture;
                        captureAllocated += resident.AllocatedBytes;
                    }
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    long copyStarted = Stopwatch.GetTimestamp();
                    bool complete = backup.Step();
                    copy += Stopwatch.GetElapsedTime(copyStarted);
                    copyAllocated += GC.GetAllocatedBytesForCurrentThread() - allocated;
                    steps++;
                    TimeSpan pause = Stopwatch.GetElapsedTime(pauseStarted);
                    maximumPause = pause > maximumPause ? pause : maximumPause;
                    if (complete)
                    {
                        backup.Dispose();
                        backup = null;
                        if (resident is null)
                        {
                            // A concurrent truncation can shorten the final batch. Repeat
                            // with a resident capture before accepting that relational image.
                            continue;
                        }
                        completed = resident;
                        Volatile.Write(ref _checkpointCopying, false);
                        break;
                    }
                }
                // The FIFO owner admits queued sessions before the next page batch.
                CheckpointStepCompleted?.Invoke(steps);
            }
            var snapshot = new WorldSnapshot(
                database,
                completed.Combat,
                completed.Generation,
                wait,
                capture,
                completed.Heat,
                completed.Tractors,
                completed.Miners,
                completed.Haulers,
                completed.Activity,
                completed.Jobs,
                copy,
                captureAllocated,
                copyAllocated,
                CheckpointSize(database),
                completed.Routines,
                completed.Economy,
                maximumPause,
                steps,
                completed.MapStatistics,
                captureFile
            );
            transferred = true;
            return snapshot;
        }
        finally
        {
            try
            {
                using (EnterPersistence("checkpoint copy cleanup"))
                {
                    backup?.Dispose();
                    Volatile.Write(ref _checkpointCopying, false);
                }
            }
            finally
            {
                if (!transferred)
                {
                    WorldSnapshot.DisposeDatabase(database, captureFile);
                }
            }
        }
    }
}
