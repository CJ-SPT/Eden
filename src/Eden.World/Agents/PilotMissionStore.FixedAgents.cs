using Eden.Data.World.Agents;
using Eden.Gameplay.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static void RequireFixedAssignment(WorldAction world, CatalogDbContext catalog, SavedMission row, MissionProfile profile)
    {
        if (profile.Definition.FixedAgentIds.IsEmpty)
        {
            return;
        }
        FixedAgentRules.RequireAvailable(
            world is EconomyTransaction,
            MissionCatalog.ForFixedAgent(catalog, row.AgentId).Any(p => p.Definition.MissionId == row.MissionId),
            world
                .Filter<SavedMission>(m => m.PilotId == row.PilotId && m.AgentId == row.AgentId && m.Id != row.Id && m.ClosedAt != null)
                .Any()
        );
    }
}
