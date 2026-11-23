using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static void RequireChainAcceptance(
        WorldAction world,
        CatalogDbContext catalog,
        CatalogAgent agent,
        SavedMission row,
        MissionProfile profile
    )
    {
        if (profile.Definition.Chain is not { } chain)
        {
            return;
        }
        if (world is not EconomyTransaction || !MissionCatalog.ChainAvailable(catalog, profile, agent))
        {
            throw new InvalidDataException("This mission chain requires the resident world and all of its verified parts.");
        }
        var previous = world
            .Filter<SavedMission>(m => m.PilotId == row.PilotId && m.AgentId == row.AgentId && m.Id < row.Id)
            .Where(m => !profile.Definition.Career || m.MissionId != row.MissionId || m.State != MissionState.Failed)
            .OrderByDescending(m => m.Id)
            .FirstOrDefault();
        MissionChainRules.RequirePredecessor(chain, previous?.MissionId, previous?.ClosedAt is null ? null : previous.State);
    }

    private SavedMission? ContinueChain(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        SavedMission completed,
        MissionProfile profile,
        long now
    )
    {
        if (profile.Definition.Chain is not { NextMissionId: > 0 } chain)
        {
            return null;
        }
        if (world is not EconomyTransaction)
        {
            throw new InvalidDataException("Mission-chain progression requires the resident world.");
        }
        var next = MissionCatalog.Find(catalog, chain.NextMissionId);
        if (!MissionCatalog.ChainAvailable(catalog, next, agent))
        {
            throw new InvalidDataException("The next mission's verified content is unavailable to this agent.");
        }
        // Closing this part, paying it and offering the next part share the resident commit.
        // The existing mission rows are the durable sequence; no parallel progress store exists.
        return Offer(world, catalog, pilot, agent, completed.Level, completed.Storyline, now, next);
    }
}
