using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private bool ActiveGang(long pilot) =>
        Live?.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work.Gang is { Phase: not RoamingGangPhase.Complete } gang
        && gang.Members.Any(member => member.PilotId == pilot && member.Status == RoamingGangMemberStatus.Active);

    private void RestoreRoamingGangs(WorldAction world)
    {
        if (Live is not { } live)
        {
            return;
        }
        // This is a cold admission pass over saved metadata. Ordinary turns use bounded member lookups.
        foreach (var row in world.Set<SavedCapsuleerHauler>().Where(row => row.Progress != null && row.Progress.Contains("\"Gang\":{")))
        {
            var work = live.HaulerCheckpoints.GetValueOrDefault(row.PilotId)?.Work ?? HaulerCheckpoint.Read(row.Progress);
            if (work?.Gang is { Phase: not RoamingGangPhase.Complete } && world.Set<SavedCapsuleerAgent>().Find(row.PilotId) is { } agent)
            {
                live.HaulerCheckpoints.TryAdd(row.PilotId, new(row.PilotId, work));
                Register(world, [agent]);
            }
        }
    }
}
