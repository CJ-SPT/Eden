using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    private WorldSnapshot? CaptureIncrementalSnapshot()
    {
        using var context = UseShared();
        SqliteConnection database = new("Data Source=:memory:;Pooling=False;Foreign Keys=True");
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
            database.Open();
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
                        using var size = database.CreateCommand();
                        size.CommandText = "SELECT page_count*page_size FROM pragma_page_count(),pragma_page_size()";
                        long bytes = Convert.ToInt64(size.ExecuteScalar(), CultureInfo.InvariantCulture);
                        transferred = true;
                        return new(
                            database,
                            resident.Combat,
                            resident.Generation,
                            wait,
                            capture,
                            resident.Heat,
                            resident.Tractors,
                            resident.Miners,
                            resident.Haulers,
                            resident.Activity,
                            resident.Jobs,
                            copy,
                            captureAllocated,
                            copyAllocated,
                            bytes,
                            resident.Routines,
                            resident.Economy,
                            maximumPause,
                            steps
                        );
                    }
                }
                // The FIFO owner admits queued sessions before the next page batch.
                CheckpointStepCompleted?.Invoke(steps);
            }
        }
        finally
        {
            using (EnterPersistence("checkpoint copy cleanup"))
            {
                backup?.Dispose();
                Volatile.Write(ref _checkpointCopying, false);
            }
            if (!transferred)
            {
                database.Dispose();
            }
        }
    }
}
