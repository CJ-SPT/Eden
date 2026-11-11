using System.Text.Json;
using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerSettlement(long PilotId, long SettledTicks);

internal sealed record CapsuleerActivation(int SystemId, long StartedTicks);

// Immutable arrays from the same owner generation as positions, jobs and paid progress.
internal sealed record CapsuleerRoutineCheckpoint(
    IReadOnlyList<CapsuleerCoarseWork> Work,
    IReadOnlyList<CapsuleerSettlement> Settled,
    IReadOnlyList<CapsuleerActivation> Activations,
    IReadOnlyList<long> Admissions
)
{
    private const int RowsPerBatch = 128;

    internal void Apply(WorldDbContext world)
    {
        foreach (var batch in Work.Chunk(RowsPerBatch))
        {
            string rows = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCapsuleerCoarseWork(SystemId,Generation,Cursor,SettledTicks,NextTicks)
                SELECT json_extract(value,'$.SystemId'),json_extract(value,'$.Generation'),json_extract(value,'$.Cursor'),
                    json_extract(value,'$.SettledTicks'),json_extract(value,'$.NextTicks')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SystemId) DO UPDATE SET Generation=excluded.Generation,Cursor=excluded.Cursor,
                    SettledTicks=excluded.SettledTicks,NextTicks=excluded.NextTicks
                """
            );
        }
        foreach (var batch in Settled.Chunk(RowsPerBatch))
        {
            string rows = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCapsuleerCoarsePilot(PilotId,SettledTicks)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.SettledTicks') FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET SettledTicks=excluded.SettledTicks
                """
            );
        }
        foreach (var batch in Activations.Chunk(RowsPerBatch))
        {
            string rows = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT OR IGNORE INTO WorldCapsuleerSimulation(SystemId,StartedTicks)
                SELECT json_extract(value,'$.SystemId'),json_extract(value,'$.StartedTicks') FROM json_each({rows})
                """
            );
        }
        foreach (var batch in Admissions.Chunk(RowsPerBatch))
        {
            string rows = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql($"INSERT OR IGNORE INTO WorldCapsuleerAdmission(PilotId) SELECT value FROM json_each({rows})");
        }
    }
}
