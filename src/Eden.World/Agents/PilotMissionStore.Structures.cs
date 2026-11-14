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
    private sealed record PendingMissionStructure(WorldNpc Npc, MissionStructureDefinition Definition);

    internal static void SeedStructures(
        WorldDbContext world,
        CatalogDbContext catalog,
        SavedMission mission,
        MissionProfile profile,
        long now
    ) => SeedStructures(new SqlWorldAction(world), catalog, mission, profile, now);

    private static void SeedStructures(WorldAction world, CatalogDbContext catalog, SavedMission mission, MissionProfile profile, long now)
    {
        List<PendingMissionStructure> pending = [];
        for (int index = 0; index < profile.Definition.Structures.Length; index++)
        {
            var definition = profile.Definition.Structures[index];
            var item = profile.Rooms.SelectMany(r => r.Objects).Single(o => o.Id == definition.ObjectId);
            var hull = new NpcProfileCatalog().Read(catalog, item.TypeId);
            var position = new SpacePosition(mission.X, mission.Y, mission.Z) + item.Position;
            var npc = new WorldNpc
            {
                TypeId = item.TypeId,
                Name = item.Name,
                SystemId = mission.SystemId,
                CorporationId = profile.EnemyCorporation,
                FactionId = profile.EnemyFaction,
                AnchorKind = NpcAnchorKind.Mission,
                Role = NpcRole.MissionStructure,
                EncounterId = MissionRules.FirstSiteId + mission.Id,
                SpawnSlot = index,
                HomeX = position.X,
                HomeY = position.Y,
                HomeZ = position.Z,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                ShieldCharge = hull.Defense.Shield,
                UpdatedAt = now,
                MotionAt = now,
            };
            world.Add(npc);
            pending.Add(new(npc, definition));
        }
        if (pending.Count == 0)
        {
            return;
        }
        // Allocate ordinary global NPC identities instead of advancing SQLite's
        // automatic NPC sequence into a separate authored structure-ID range.
        world.SaveChanges();
        foreach (var member in pending)
        {
            world.Add(
                new SavedMissionStructure
                {
                    NpcId = member.Npc.Id,
                    MissionId = mission.Id,
                    ObjectId = member.Definition.ObjectId,
                    Required = member.Definition.Required,
                    CompletesMission = member.Definition.CompletesMission,
                }
            );
        }
    }

    private static MissionStructureProgress[] StructureProgress(WorldDbContext world, long missionId)
    {
        _ = world.Set<SavedMissionStructure>().Where(s => s.MissionId == missionId).ToArray();
        return world
            .Set<SavedMissionStructure>()
            .Local.Where(s => s.MissionId == missionId)
            .Select(s => new MissionStructureProgress(s.NpcId, s.Required, s.CompletesMission, s.DestroyedAt.HasValue))
            .ToArray();
    }

    private static bool EnemiesComplete(WorldDbContext world, SavedMission mission, MissionProfile profile)
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
        _ = world.Set<WorldNpc>().Where(n => required.Contains(n.Id)).ToArray();
        return !world.Set<WorldNpc>().Local.Any(n => required.Contains(n.Id) && n.DestroyedAt == null && n.RetiredAt == null);
    }

    private static void StructureKilled(WorldDbContext world, CatalogDbContext catalog, SavedMission mission, WorldNpc npc, long now)
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

    private static StaticSpaceObject RecoveryContainer(
        CatalogDbContext catalog,
        SavedMission mission,
        MissionProfile profile,
        int index,
        bool empty
    )
    {
        var definition = profile.Definition.Structures[index];
        var source = profile.Rooms.SelectMany(r => r.Objects).Single(o => o.Id == definition.ObjectId);
        var type = catalog.RequireItem(MissionStructureRules.RecoveryContainerType);
        return new(
            MissionContainerRules.Id(mission.Id, profile.Definition.Containers.Length + index),
            type.Id,
            type.GroupId,
            (int)InventoryCategory.Celestial,
            checked((int)mission.PilotId),
            new SpacePosition(mission.X, mission.Y, mission.Z) + source.Position,
            (float)(type.Radius ?? 0)
        )
        {
            IsGlobal = false,
            Name = (source.Name ?? catalog.RequireItem(source.TypeId).Name) + " cargo",
            Container = new(mission.PilotId, empty),
        };
    }
}
