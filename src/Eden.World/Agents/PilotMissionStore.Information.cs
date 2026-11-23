using Eden.Data.World.Agents;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    public SpacePosition? EntryPoint(long pilotId, long agentId)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("The world is not running.");
        return live.Economy.Inspect(world =>
        {
            if (PreviewMission(world, pilotId, agentId) is not { State: MissionState.Accepted } mission)
            {
                return null;
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            return MissionCatalog.Find(catalog, mission.MissionId) is { DungeonId: not null, Definition.Scan: null }
                ? new SpacePosition(mission.X, mission.Y, mission.Z)
                : null;
        });
    }

    public MissionShipRestrictions ShipRestrictions(long pilotId, long agentId, int dungeonId, long? gateId = null)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("The world is not running.");
        return live.Economy.Inspect(world =>
        {
            var mission =
                PreviewMission(world, pilotId, agentId) ?? throw new InvalidDataException("Choose your current mission's dungeon.");
            using CatalogDbContext catalog = new(database.CatalogPath);
            if (MissionCatalog.Find(catalog, mission.MissionId).DungeonId != dungeonId)
            {
                throw new InvalidDataException("Choose your current mission's dungeon.");
            }
            return MissionShipCatalog.Read(catalog, mission.MissionId, gateId);
        });
    }

    private SavedMission? PreviewMission(WorldAction world, long pilotId, long agentId)
    {
        _ = ResidentPilot(world, pilotId);
        _ = Agent(agentId);
        long now = Now;
        // Agency previews observe deadlines without expiring offers, generating rooms or
        // settling rewards. They must use the same resident generation as the mission.
        return world
            .Filter<SavedMission>(m =>
                m.PilotId == pilotId
                && m.AgentId == agentId
                && m.ClosedAt == null
                && m.ExpiresAt > now
                && (m.State == MissionState.Offered || m.State == MissionState.Accepted)
            )
            .SingleOrDefault();
    }
}
