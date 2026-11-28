using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial record HaulerCheckpoint
{
    internal static HaulerProgress? Read(WorldAction world, LiveWorld live, long pilot) =>
        live.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work
        ?? Read(world.Filter<SavedCapsuleerHauler>(row => row.PilotId == pilot).FirstOrDefault()?.Progress);

    internal void Apply(WorldAction world)
    {
        var row = world.Set<SavedCapsuleerHauler>().Single(value => value.PilotId == PilotId);
        row.Progress = JsonSerializer.Serialize(Work, Options);
    }
}
