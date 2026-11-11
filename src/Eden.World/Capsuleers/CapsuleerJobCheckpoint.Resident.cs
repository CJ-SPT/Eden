using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial record CapsuleerJobCheckpoint
{
    internal static void Apply(WorldAction world, CapsuleerJob job)
    {
        var row = world.Set<SavedCapsuleerJob>().Find(job.PilotId) ?? world.Add(new SavedCapsuleerJob { PilotId = job.PilotId }).Entity;
        row.Revision = job.Revision;
        row.Sequence = job.Sequence;
        row.DueTicks = job.DueTicks;
        row.Action = job.Action;
    }
}
