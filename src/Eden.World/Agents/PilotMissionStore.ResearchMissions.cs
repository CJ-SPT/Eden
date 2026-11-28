using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Gameplay.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static SavedResearchProject? FindResearchProject(WorldAction world, long pilotId, long agentId) =>
        world.Filter<SavedResearchProject>(project => project.PilotId == pilotId && project.AgentId == agentId).SingleOrDefault();

    private static SavedMission? LatestResearchMission(WorldAction world, long pilotId, long agentId) =>
        world
            .Filter<SavedMission>(mission => mission.PilotId == pilotId && mission.AgentId == agentId)
            .OrderByDescending(mission => mission.Id)
            .FirstOrDefault();

    private static bool ResearchMissionAvailable(SavedMission? previous, long now)
    {
        if (previous is null)
        {
            return true;
        }
        long closedAt =
            previous.State == MissionState.Failed
                ? previous.ExpiresAt
                : Math.Max(previous.AcceptedAt ?? previous.OfferedAt, previous.ClosedAt ?? previous.ExpiresAt);
        return now >= ResearchRules.NextMissionAt(previous.Id, closedAt);
    }

    private static SavedResearchProject RequireResearchMissionProject(WorldAction world, Pilot pilot, CatalogAgent agent) =>
        ResearchMissionCatalog.Supports(agent) && world is EconomyTransaction
            ? FindResearchProject(world, pilot.Id, agent.Id)
                ?? throw new InvalidDataException("Start a research project with this agent to request a research mission.")
            : throw new InvalidDataException("This agent's research missions require supported content and the resident world.");

    private static MissionProfile ResearchMissionProfile(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        long now
    )
    {
        var previous = LatestResearchMission(world, pilot.Id, agent.Id);
        if (!ResearchMissionAvailable(previous, now))
        {
            throw new InvalidDataException("This research agent has no new mission available yet.");
        }
        return ResearchMissionCatalog.Next(catalog, agent, previous?.MissionId);
    }

    private static double? ResearchMissionPoints(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        SavedMission row
    ) =>
        ResearchMissionCatalog.IsResearch(row.MissionId)
        && row.ClosedAt is null
        && FindResearchProject(world, pilot.Id, agent.Id) is { } project
            ? ResearchRate(world, catalog, pilot, agent, project)
            : null;

    private static double PayResearchMission(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogAgent agent,
        SavedResearchProject project,
        SavedMission mission,
        long now,
        List<MissionStandingChange> standings
    )
    {
        var settled = SettleResearch(world, catalog, pilot, agent, project, now);
        project.Points = (double)((decimal)settled.Points + (decimal)settled.PointsPerDay);
        ApplyMissionStanding(world, pilot.Id, agent.Id, mission.CorporationFraction, now, agent.CorporationId, mission.SystemId, standings);
        ApplyMissionStanding(
            world,
            pilot.Id,
            agent.CorporationId,
            mission.CorporationFraction,
            now,
            agent.CorporationId,
            mission.SystemId,
            standings
        );
        project.PointsPerDay = ResearchRate(world, catalog, pilot, agent, project);
        return settled.PointsPerDay;
    }

    private static MissionState? CloseResearchMission(WorldAction world, CatalogDbContext catalog, long pilotId, long agentId, long now)
    {
        if (LatestResearchMission(world, pilotId, agentId) is not { ClosedAt: null } mission)
        {
            return null;
        }
        if (world is not EconomyTransaction)
        {
            throw new InvalidDataException("Ending a research mission requires the resident world.");
        }
        if (!Expire(world, catalog, mission, now))
        {
            Close(world, mission, mission.State == MissionState.Offered ? MissionState.Declined : MissionState.Quit, now);
        }
        return mission.State;
    }
}
