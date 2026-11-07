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
    Eden.World.Capsuleers.MinerCheckpoint? miner = null
) : IDisposable
{
    internal SqliteConnection Database => database;
    internal long Generation => generation;
    internal TimeSpan Wait => wait;
    internal TimeSpan Capture => capture;

    internal TimeSpan Project()
    {
        long started = Stopwatch.GetTimestamp();
        if (combat is not null)
        {
            using WorldDbContext world = new(database);
            combat.Apply(world);
        }
        if (miner is not null)
        {
            using WorldDbContext world = new(database);
            miner.Apply(world);
        }
        return Stopwatch.GetElapsedTime(started);
    }

    public void Dispose() => database.Dispose();
}
