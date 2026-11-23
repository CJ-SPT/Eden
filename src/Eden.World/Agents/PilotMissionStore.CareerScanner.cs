using Eden.Data.Catalog;
using Eden.Data.World.Agents;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private const long AgentSequenceSize = 1_000_000;

    private static CombatAnomaly? CareerScanner(
        CatalogDbContext catalog,
        SavedMission mission,
        MissionProfile profile,
        MissionRoomProfile room,
        IReadOnlyList<StaticSpaceObject> objects
    )
    {
        if (profile.Definition.Scan is not { } scan || room != profile.Rooms[0])
        {
            return null;
        }
        int dungeonId = profile.DungeonId!.Value;
        var dungeon = catalog.Require(CatalogData.Client.Dungeons, (long)dungeonId);
        return new(
            MissionRules.FirstSiteId + mission.Id,
            ScannerTargetIds.FromMissionSequence(mission.AgentId % AgentSequenceSize),
            mission.SystemId,
            dungeonId,
            checked((int)dungeon.DungeonNameID!.Value),
            1,
            new(mission.X, mission.Y, mission.Z),
            true,
            objects,
            FactionId: profile.EnemyFaction,
            IsSignature: scan != MissionScanKind.Anomaly,
            Exploration: scan switch
            {
                MissionScanKind.Data => ExplorationKind.Data,
                MissionScanKind.Relic => ExplorationKind.Relic,
                _ => null,
            },
            Mining: scan == MissionScanKind.Gas ? MiningFamily.Gas : null
        )
        {
            ArchetypeId = checked((int?)dungeon.ArchetypeID),
            CareerTraining = true,
        };
    }

    internal static bool TrainingSignatureAvailable(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long siteId,
        int system,
        long now
    ) =>
        world is EconomyTransaction
        && world.Set<SavedMission>().Find(siteId - MissionRules.FirstSiteId) is { State: MissionState.Accepted, ClosedAt: null } mission
        && mission.PilotId == pilotId
        && mission.SystemId == system
        && mission.ExpiresAt > now
        && MissionCatalog.Find(catalog, mission.MissionId).Definition.Scan
            is MissionScanKind.Data
                or MissionScanKind.Relic
                or MissionScanKind.Gas;
}
