using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static MissionStructureProgress[] StructureProgress(WorldAction world, long missionId)
    {
        _ = world.Set<SavedMissionStructure>().Where(s => s.MissionId == missionId).ToArray();
        return world
            .Set<SavedMissionStructure>()
            .Local.Where(s => s.MissionId == missionId)
            .Select(s => new MissionStructureProgress(s.NpcId, s.Required, s.CompletesMission, s.DestroyedAt.HasValue))
            .ToArray();
    }

    private static void StructureKilled(WorldAction world, CatalogDbContext catalog, SavedMission mission, WorldNpc npc, long now)
    {
        var saved =
            world.Set<SavedMissionStructure>().Find(npc.Id)
            ?? throw new InvalidDataException("The mission structure has no saved membership.");
        if (saved.DestroyedAt.HasValue)
        {
            return;
        }
        saved.DestroyedAt = now;
        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        int index = Array.FindIndex(profile.Definition.Structures.ToArray(), s => s.ObjectId == saved.ObjectId);
        var definition = profile.Definition.Structures[index];
        if (definition.Drop is { } drop)
        {
            // This is a mission-owned recovery container, not a wreck or rerollable NPC drop.
            InventoryPersistence.MergeOutput(
                world,
                catalog,
                mission.PilotId,
                MissionContainerRules.Id(mission.Id, profile.Definition.Containers.Length + index),
                (int)InventoryFlag.Cargo,
                drop.TypeId,
                drop.Quantity
            );
        }
        if (!mission.ObjectiveComplete)
        {
            mission.ObjectiveComplete = MissionStructureRules.Complete(
                EnemiesComplete(world, mission, profile),
                StructureProgress(world, mission.Id)
            );
        }
    }

    private static bool EnemiesComplete(WorldAction world, SavedMission mission, MissionProfile profile)
    {
        bool started = Enumerable
            .Range(0, profile.Definition.Waves.Length)
            .Where(w => profile.Definition.Waves[w].Groups.Any(g => g.Required))
            .All(w =>
                (
                    world.Set<SavedMissionRoom>().Find(mission.Id, profile.WaveRoom(w))?.Wave
                    ?? (profile.Rooms.Length == 1 ? mission.Wave : -1)
                ) >= w
            );
        if (!started)
        {
            return false;
        }
        _ = world.Set<SavedMissionMember>().Where(m => m.MissionId == mission.Id).ToArray();
        var required = world
            .Set<SavedMissionMember>()
            .Local.Where(m => m.MissionId == mission.Id && m.Required)
            .Select(m => m.NpcId)
            .ToHashSet();
        _ = world.Set<WorldNpc>().Where(n => Enumerable.Contains(required, n.Id)).ToArray();
        return !world.Set<WorldNpc>().Local.Any(n => Enumerable.Contains(required, n.Id) && n.DestroyedAt == null && n.RetiredAt == null);
    }
}
