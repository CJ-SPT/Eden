using System.Diagnostics;
using Eden.World.Maps;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
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
    EconomyCheckpoint? economy = null,
    TimeSpan maximumCopyPause = default,
    int copySteps = 1,
    MapStatisticsCheckpoint? mapStatistics = null,
    string? captureFile = null
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
    internal TimeSpan MaximumCopyPause => maximumCopyPause;
    internal int CopySteps => copySteps;
    internal long ProjectionAllocatedBytes { get; private set; }
    internal IReadOnlyList<WorldTableProjection>? ProjectionTables { get; private set; }
    internal EconomyProjection<EconomyNpcMarketOrder>? NpcMarketOrders =>
        economy?.Tables.OfType<EconomyProjection<EconomyNpcMarketOrder>>().SingleOrDefault();

    internal TimeSpan Project() => Project(measure: false);

    internal TimeSpan Project(bool measure)
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
                || mapStatistics is not null
            )
            {
                using WorldDbContext world = new(database);
                using var transaction = world.Database.BeginTransaction();
                var metrics = measure ? new WorldProjectionMetrics() : null;
                economy?.Apply(world, metrics);
                combat?.Apply(world, metrics);
                using (metrics?.Measure("WorldShipHeat", heat?.Ships.Count ?? 0))
                {
                    heat?.Apply(world);
                }
                using (metrics?.Measure("WorldPosition (tractors)", tractors?.Positions.Count ?? 0))
                {
                    tractors?.Apply(world);
                }
                using (metrics?.Measure("WorldCapsuleerCommunity", miners?.Count ?? 0))
                {
                    Eden.World.Capsuleers.MinerCheckpoint.Apply(world, miners ?? []);
                }
                using (metrics?.Measure("WorldCapsuleerHauler", haulers?.Count ?? 0))
                {
                    Eden.World.Capsuleers.HaulerCheckpoint.Apply(world, haulers ?? []);
                }
                using (metrics?.Measure("WorldCapsuleerJob", jobs?.Jobs.Count ?? 0))
                {
                    jobs?.Apply(world);
                }
                routines?.Apply(world);
                activity?.Apply(world);
                mapStatistics?.Apply(world);
                transaction.Commit();
                ProjectionTables = metrics?.Tables;
            }
            return Stopwatch.GetElapsedTime(started);
        }
        finally
        {
            ProjectionAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
    }

    public void Dispose() => DisposeDatabase(database, captureFile);

    internal static void DisposeDatabase(SqliteConnection? image, string? path)
    {
        try
        {
            image?.Dispose();
        }
        finally
        {
            if (path is not null)
            {
                CheckpointCaptureFile.Delete(path);
            }
        }
    }
}
