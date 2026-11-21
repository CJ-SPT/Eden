using System.Text.Json;
using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial record CapsuleerJobCheckpoint(IReadOnlyList<CapsuleerJob> Jobs)
{
    internal void Apply(WorldDbContext world)
    {
        foreach (var batch in Jobs.Chunk(CapsuleerCheckpointJson.ProjectionBatchSize))
        {
            ApplyBatch(world, JsonSerializer.Serialize(batch));
        }
    }

    internal static void ApplyBatch(WorldDbContext world, string values) =>
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldCapsuleerJob(PilotId,Revision,Sequence,DueTicks,Action)
            WITH payload(value) AS MATERIALIZED (SELECT jsonb(value) FROM json_each({values}))
            SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.Revision'),json_extract(value,'$.Sequence'),
                json_extract(value,'$.DueTicks'),json_extract(value,'$.Action')
            FROM payload WHERE 1
            ON CONFLICT(PilotId) DO UPDATE SET Revision=excluded.Revision,Sequence=excluded.Sequence,
                DueTicks=excluded.DueTicks,Action=excluded.Action
            """
        );

    internal static void Apply(WorldDbContext world, CapsuleerJob job) =>
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldCapsuleerJob(PilotId,Revision,Sequence,DueTicks,Action)
            VALUES ({job.PilotId},{job.Revision},{job.Sequence},{job.DueTicks},{job.Action})
            ON CONFLICT(PilotId) DO UPDATE SET Revision=excluded.Revision,Sequence=excluded.Sequence,
                DueTicks=excluded.DueTicks,Action=excluded.Action
            """
        );
}
