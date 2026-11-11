using System.Diagnostics;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;

namespace Eden.World.Runtime;

internal sealed class WorldSnapshot(
    SqliteConnection database,
    CombatCheckpoint? combat,
    long generation,
    TimeSpan wait,
    TimeSpan capture,
    ShipHeatCheckpoint? heat = null,
    TractorCheckpoint? tractors = null,
    IReadOnlyList<Eden.World.Capsuleers.MinerCheckpoint>? miners = null,
    IReadOnlyList<Eden.World.Capsuleers.HaulerCheckpoint>? haulers = null,
    Eden.World.Capsuleers.CapsuleerClockCheckpoint? activity = null,
    Eden.World.Capsuleers.CapsuleerJobCheckpoint? jobs = null,
    TimeSpan copy = default,
    long captureAllocatedBytes = 0,
    long copyAllocatedBytes = 0,
    long copyBytes = 0,
    Eden.World.Capsuleers.CapsuleerRoutineCheckpoint? routines = null,
    Eden.World.Runtime.Economy.EconomyCheckpoint? economy = null
) : IDisposable
{
    internal SqliteConnection Database => database;
    internal long Generation => generation;
    internal TimeSpan Wait => wait;
    internal TimeSpan Capture => capture;
    internal TimeSpan Copy => copy;
    internal long CaptureAllocatedBytes => captureAllocatedBytes;
    internal long CopyAllocatedBytes => copyAllocatedBytes;
    internal long CopyBytes => copyBytes;
    internal long ProjectionAllocatedBytes { get; private set; }

    internal TimeSpan Project()
    {
        long started = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            if (
                combat is not null
                || heat is not null
                || tractors is not null
                || miners is not null
                || haulers is not null
                || activity is not null
                || jobs is not null
                || routines is not null
                || economy is not null
            )
            {
                using WorldDbContext world = new(database);
                using var transaction = world.Database.BeginTransaction();
                economy?.Apply(world);
                combat?.Apply(world);
                heat?.Apply(world);
                tractors?.Apply(world);
                Eden.World.Capsuleers.MinerCheckpoint.Apply(world, miners ?? []);
                Eden.World.Capsuleers.HaulerCheckpoint.Apply(world, haulers ?? []);
                jobs?.Apply(world);
                routines?.Apply(world);
                activity?.Apply(world);
                transaction.Commit();
            }
            return Stopwatch.GetElapsedTime(started);
        }
        finally
        {
            ProjectionAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
    }

    public void Dispose() => database.Dispose();
}
