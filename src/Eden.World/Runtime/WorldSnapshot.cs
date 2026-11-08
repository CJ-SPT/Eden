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
    TractorCheckpoint? tractors = null
) : IDisposable
{
    internal SqliteConnection Database => database;
    internal long Generation => generation;
    internal TimeSpan Wait => wait;
    internal TimeSpan Capture => capture;

    internal TimeSpan Project()
    {
        long started = Stopwatch.GetTimestamp();
        if (combat is not null || heat is not null || tractors is not null)
        {
            using WorldDbContext world = new(database);
            using var transaction = world.Database.BeginTransaction();
            combat?.Apply(world);
            heat?.Apply(world);
            tractors?.Apply(world);
            transaction.Commit();
        }
        return Stopwatch.GetElapsedTime(started);
    }

    public void Dispose() => database.Dispose();
}
