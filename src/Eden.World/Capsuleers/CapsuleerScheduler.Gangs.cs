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
            || work?.CorporationSupply is not null
            || work?.SovereigntyExpedition is not null
            || work?.MilitiaPatrol is not null
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
                    row.Progress != null
                    && (
                        row.Progress.Contains("\"Gang\":{")
                        || row.Progress.Contains("\"RegionalEscort\":{")
                        || row.Progress.Contains("\"CorporationSupply\":{")
                        || row.Progress.Contains("\"MilitiaPatrol\":{")
                    )
                )
        )
        {
            if (_suspended.Contains(row.PilotId) || live.HaulerCheckpoints.ContainsKey(row.PilotId))
            {
                continue;
            }
            var work = HaulerCheckpoint.Read(row.Progress);
            if (
                (
                    work?.Gang is { Phase: not RoamingGangPhase.Complete }
                    || work?.RegionalEscort is not null
                    || work?.CorporationSupply is not null
                    || work?.MilitiaPatrol is not null
                )
                && world.Set<SavedCapsuleerAgent>().Find(row.PilotId) is { } agent
                && (CapsuleerCorporations.Organization(world, row.PilotId).ActiveWorker ?? agent.Career) == CapsuleerCareer.Hauling
            )
            {
                HaulerCheckpoint checkpoint = new(agent.PilotId, work);
                live.HaulerCheckpoints.Add(agent.PilotId, checkpoint);
                if (work.MilitiaPatrol is { Phase: not MilitiaPatrolPhase.Returning })
                {
                    live.MilitiaPatrolPilots.Add(agent.PilotId);
                }
                live.ProjectedHaulers.TryAdd(agent.PilotId, checkpoint);
            }
        }
    }
}
