using Eden.Data.Catalog;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static bool Expire(WorldAction world, SavedMission mission, long now)
    {
        if (mission.ClosedAt is null && now >= mission.ExpiresAt)
        {
            Close(world, mission, MissionState.Failed, now);
            return true;
        }
        return false;
    }

    private static void Close(WorldAction world, SavedMission mission, MissionState state, long now)
    {
        if (MissionCatalog.IsMining(mission.MissionId))
        {
            world.RemoveRange(MissionDeposits(world.Set<WorldAsteroid>(), mission.Id));
        }
        long firstContainer = MissionContainerRules.Id(mission.Id, 0);
        long endContainer = checked(firstContainer + MissionRules.NpcsPerMission);
        world.RemoveRange(
            world.Set<Eden.Data.World.Inventory.OwnedItem>().Where(i => i.LocationId >= firstContainer && i.LocationId < endContainer)
        );
        mission.State = state;
        mission.ClosedAt = now;
        foreach (
            var npc in world
                .Set<WorldNpc>()
                .Where(n =>
                    n.AnchorKind == Eden.Gameplay.Combat.NpcAnchorKind.Mission
                    && n.EncounterId == MissionRules.FirstSiteId + mission.Id
                    && n.DestroyedAt == null
                    && n.RetiredAt == null
                )
        )
        {
            npc.RetiredAt = now;
        }
    }
}
