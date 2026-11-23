using Eden.Data.World.Agents;
using Eden.Gameplay.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    public IReadOnlyList<long> AvailableEventAgents(long pilotId, IReadOnlySet<long> agentIds)
    {
        var agents = agentIds.Select(directory.Agent).OfType<CatalogAgent>().Where(a => a.Type == AgentType.Event).ToArray();
        if (agents.Length == 0)
        {
            return [];
        }
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("The world is not running.");
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(world =>
        {
            var pilot = ResidentPilot(world, pilotId);
            long now = Now;
            var active = world
                .Filter<SavedMission>(m =>
                    m.PilotId == pilotId
                    && agentIds.Contains(m.AgentId)
                    && m.ClosedAt == null
                    && m.ExpiresAt > now
                    && (m.State == MissionState.Offered || m.State == MissionState.Accepted)
                )
                .Select(m => m.AgentId)
                .ToHashSet();
            return agents.Where(a => active.Contains(a.Id) || Access(world, catalog, pilot, a, now).Allowed).Select(a => a.Id).ToArray();
        });
    }

    private static void RequireFixedAssignment(WorldAction world, CatalogDbContext catalog, SavedMission row, MissionProfile profile)
    {
        if (profile.Definition.Scripted || profile.Definition.FixedAgentIds.IsEmpty)
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
