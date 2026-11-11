using System.Text;
using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial record CapsuleerJobCheckpoint(IReadOnlyList<CapsuleerJob> Jobs)
{
    internal void Apply(WorldDbContext world)
    {
        const int rowsPerBatch = 128;
        foreach (var batch in Jobs.Chunk(rowsPerBatch))
        {
            StringBuilder sql = new("INSERT INTO WorldCapsuleerJob(PilotId,Revision,Sequence,DueTicks,Action) VALUES ");
            List<object> parameters = [];
            foreach (var job in batch)
            {
                if (parameters.Count > 0)
                {
                    sql.Append(',');
                }
                int offset = parameters.Count;
                sql.Append($"(@p{offset},@p{offset + 1},@p{offset + 2},@p{offset + 3},@p{offset + 4})");
                parameters.AddRange([job.PilotId, job.Revision, job.Sequence, job.DueTicks, (int)job.Action]);
            }
            sql.Append(" ON CONFLICT(PilotId) DO UPDATE SET Revision=excluded.Revision,Sequence=excluded.Sequence,");
            sql.Append("DueTicks=excluded.DueTicks,Action=excluded.Action");
            world.Database.ExecuteSqlRaw(sql.ToString(), parameters.ToArray());
        }
    }

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
