using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerJobCheckpoint(IReadOnlyList<CapsuleerJob> Jobs)
{
    internal void Apply(WorldDbContext world)
    {
        foreach (var job in Jobs)
        {
            Apply(world, job);
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
