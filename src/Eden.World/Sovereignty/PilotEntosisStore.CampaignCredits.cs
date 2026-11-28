using Eden.Data.World.Sovereignty;
using Eden.World.Runtime.Economy;

namespace Eden.World.Sovereignty;

internal sealed partial class PilotEntosisStore
{
    private void RecordCampaignContribution(
        WorldAction world,
        SavedSovereigntyConflict conflict,
        IReadOnlyList<SavedEntosisCycle> cycles,
        long completedAt
    )
    {
        foreach (var cycle in cycles)
        {
            if (
                cycle.Contributing
                && cycle.AllianceId > 0
                && cycle.AllianceId != conflict.DefenderAllianceId
                && Math.Max(cycle.StartedAt, cycle.WarmupUntil) < completedAt
                && cycle.EndsAt >= completedAt
                && _scoringLeases.GetValueOrDefault(cycle.ModuleId).HasValue
                && world.Set<SavedSovereigntyCampaignCredit>().Find(conflict.HubId, cycle.AllianceId) is null
            )
            {
                world.Add(
                    new SavedSovereigntyCampaignCredit
                    {
                        HubId = conflict.HubId,
                        AllianceId = cycle.AllianceId,
                        SystemId = conflict.SystemId,
                    }
                );
            }
        }
    }

    private static void DiscardPendingCampaignCredits(WorldAction world, long hubId) =>
        world.RemoveRange(
            world.Filter<SavedSovereigntyCampaignCredit>(row => row.HubId == hubId).Where(row => row.VictoryAt == null).ToArray()
        );
}
