using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private bool ActiveOperation(long pilot)
    {
        var work = Live?.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work;
        return work?.Gang is { Phase: not RoamingGangPhase.Complete } gang
                && gang.Members.Any(member => member.PilotId == pilot && member.Status == RoamingGangMemberStatus.Active)
            || work?.RegionalEscort is { } escort && work.ShipId == escort.ShipId && work.ActiveTicks < escort.Operation.ExpiresAt;
    }

    private void RestoreRoamingGangs(WorldAction world)
    {
        if (Live is not { } live)
        {
            return;
        }
        // This is a cold admission pass over saved metadata. Ordinary turns use bounded member lookups.
        foreach (
            var row in world
                .Set<SavedCapsuleerHauler>()
                .Where(row =>
                    row.Progress != null && (row.Progress.Contains("\"Gang\":{") || row.Progress.Contains("\"RegionalEscort\":{"))
                )
        )
        {
            var work = live.HaulerCheckpoints.GetValueOrDefault(row.PilotId)?.Work ?? HaulerCheckpoint.Read(row.Progress);
            if (
                (work?.Gang is { Phase: not RoamingGangPhase.Complete } || work?.RegionalEscort is not null)
                && world.Set<SavedCapsuleerAgent>().Find(row.PilotId) is { } agent
            )
            {
                live.HaulerCheckpoints.TryAdd(row.PilotId, new(row.PilotId, work));
                Register(world, [agent]);
            }
        }
    }
}
