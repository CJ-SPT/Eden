using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    internal static void CompleteEpicHack(WorldAction world, CatalogDbContext catalog, SavedHackingContainer container, long now)
    {
        if (
            world.Set<SavedMission>().Find(container.SiteId - MissionRules.FirstSiteId)
                is not { State: MissionState.Accepted, ObjectiveComplete: false } mission
            || mission.PilotId != container.OwnerId
            || EpicArcCatalog.Find(mission.MissionId) is not { } definition
            || container.Index < 0
            || container.Index >= definition.Containers.Length
            || definition.Containers[container.Index] is not { CompletesOnHack: true } objective
        )
        {
            return;
        }

        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        SpawnTriggeredWaves(world, catalog, mission, profile, MissionWaveTrigger.HackResolved, objective.ObjectId, now);
        mission.ObjectiveComplete = EpicHacksReady(world, mission, profile) && EnemiesComplete(world, mission, profile);
    }

    private static bool EpicHacksReady(WorldAction world, SavedMission mission, MissionProfile profile)
    {
        var required = profile
            .Definition.Containers.Select((c, index) => (Container: c, Index: index))
            .Where(pair => pair.Container.CompletesOnHack)
            .Select(pair => MissionContainerRules.Id(mission.Id, pair.Index))
            .ToArray();
        bool Hacked(long id) => world.Set<SavedHackingContainer>().Find(id)?.Hacked == true;
        return required.Length == 0 || (profile.Definition.RequiresAllHacks ? required.All(Hacked) : required.Any(Hacked));
    }

    private static bool EpicCombatObjectivesReady(WorldAction world, SavedMission mission, MissionProfile profile) =>
        profile.Definition.EpicArcId is null
        || profile.Definition.CareerVisit is null
            && !profile.Definition.Containers.Any(c => c.CompletesOnTake || c.DepositTypeId > 0 && c.DepositCompletesMission)
            && EpicHacksReady(world, mission, profile);
}
