using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    internal static void Killed(WorldAction world, CatalogDbContext catalog, WorldNpc npc, long now)
    {
        if (npc.AnchorKind != NpcAnchorKind.Mission)
        {
            return;
        }
        var mission =
            world.Set<SavedMission>().Find(npc.EncounterId - MissionRules.FirstSiteId)
            ?? throw new InvalidDataException("Mission NPC has no saved mission.");
        if (mission.State != MissionState.Accepted || Expire(world, mission, now))
        {
            return;
        }
        if (npc.Role == NpcRole.MissionStructure)
        {
            StructureKilled(world, catalog, mission, npc, now);
            return;
        }
        var member = world.Set<SavedMissionMember>().Find(npc.Id)!;
        _ = world.Set<SavedMissionMember>().Where(m => m.MissionId == mission.Id).ToArray();
        var members = world
            .Set<SavedMissionMember>()
            .Local.Where(m => m.MissionId == mission.Id)
            .Select(m => new MissionEnemyProgress(m.NpcId, m.Wave, m.Trigger, m.Required, m.CompletesMission))
            .ToArray();
        var alive = world
            .Set<WorldNpc>()
            .Where(n =>
                n.AnchorKind == NpcAnchorKind.Mission
                && n.EncounterId == MissionRules.FirstSiteId + mission.Id
                && n.Id != npc.Id
                && n.DestroyedAt == null
                && n.RetiredAt == null
            )
            .ToArray()
            .Where(n => n.DestroyedAt is null && n.RetiredAt is null)
            .Select(n => n.Id)
            .ToHashSet();
        alive.UnionWith(
            world
                .Set<WorldNpc>()
                .Local.Where(n =>
                    n.AnchorKind == NpcAnchorKind.Mission
                    && n.EncounterId == MissionRules.FirstSiteId + mission.Id
                    && n.Id != npc.Id
                    && n.DestroyedAt == null
                    && n.RetiredAt == null
                )
                .Select(n => n.Id)
        );
        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        if (
            member.LootQuantity > 0
            && world
                .Set<SavedMissionMember>()
                .Local.Any(m =>
                    m.MissionId == mission.Id
                    && m.Wave == member.Wave
                    && m.NpcId != npc.Id
                    && m.LootTypeId == member.LootTypeId
                    && m.LootQuantity > 0
                    && Enumerable.Contains(alive, m.NpcId)
                )
        )
        {
            member.LootQuantity = 0;
        }
        if (mission.ObjectiveComplete)
        {
            return;
        }
        int roomId = profile.WaveRoom(member.Wave);
        var progress = world.Set<SavedMissionRoom>().Find(mission.Id, roomId);
        int currentWave = progress?.Wave ?? mission.Wave;
        int nextWave = Enumerable
            .Range(currentWave + 1, profile.Definition.Waves.Length - currentWave - 1)
            .FirstOrDefault(w => profile.WaveRoom(w) == roomId, -1);
        bool allRequiredSpawned = Enumerable
            .Range(0, profile.Definition.Waves.Length)
            .Where(w => profile.Definition.Waves[w].Groups.Any(g => g.Required))
            .All(w =>
                (
                    world.Set<SavedMissionRoom>().Find(mission.Id, profile.WaveRoom(w))?.Wave
                    ?? (profile.Rooms.Length == 1 ? mission.Wave : -1)
                ) >= w
            );
        var decision = MissionEncounterRules.Killed(
            members.Single(m => m.Id == member.NpcId),
            currentWave,
            nextWave < 0 ? null : nextWave,
            members,
            alive,
            allRequiredSpawned
        );
        if (decision.SpawnNextWave)
        {
            Spawn(world, catalog, mission, nextWave, now);
        }
        if (decision.Complete)
        {
            mission.ObjectiveComplete =
                member.CompletesMission || MissionStructureRules.Complete(true, StructureProgress(world, mission.Id));
        }
    }

    private static void Spawn(WorldAction world, CatalogDbContext catalog, SavedMission mission, int wave, long now)
    {
        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        int roomId = profile.WaveRoom(wave);
        var room = profile.Rooms.Single(r => r.Id == roomId);
        var groups = profile.Definition.Waves[wave].Groups;
        List<MissionPreparedSpawn> spawns = [];
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            for (int i = 0; i < group.Count; i++)
            {
                int slot = spawns.Count;
                if (slot >= MissionRules.MembersPerWave)
                {
                    throw new InvalidDataException("Mission wave exceeds its reserved NPC identity range.");
                }
                uint seed = NpcSpawnRules.Seed(mission.SystemId, wave, checked((int)mission.Id + slot));
                int type = group.Types[(int)(seed % group.Types.Length)];
                var npcProfile = new NpcProfileCatalog().Read(catalog, type);
                spawns.Add(
                    new(
                        group,
                        new(type, groupIndex, group.Range, npcProfile.Type.GroupId == NpcEncounterDefinitions.SentryGroup),
                        npcProfile
                    )
                );
            }
        }
        bool initial = !Enumerable.Range(0, wave).Any(w => profile.WaveRoom(w) == roomId);
        var placements = MissionSpawnRules.Place(initial, spawns.Select(s => s.Member).ToArray(), room.SpawnAnchors);
        for (int slot = 0; slot < spawns.Count; slot++)
        {
            var spawn = spawns[slot];
            var group = spawn.Group;
            var npcProfile = spawn.Profile;
            int type = spawn.Member.TypeId;
            int faction = group.EnemyFaction == 0 ? profile.EnemyFaction : group.EnemyFaction;
            int corporation = checked((int)(catalog.Read(CatalogData.Client.Factions, (long)faction)?.CorporationID ?? 0));
            long id = checked(
                MissionRules.FirstNpcId + mission.Id * MissionRules.NpcsPerMission + wave * MissionRules.MembersPerWave + slot
            );
            var p = new SpacePosition(mission.X, mission.Y, mission.Z) + (room.Position + placements[slot].Position);
            world.Add(
                new WorldNpc
                {
                    Id = id,
                    TypeId = type,
                    SystemId = mission.SystemId,
                    CorporationId = corporation,
                    FactionId = faction,
                    AnchorKind = NpcAnchorKind.Mission,
                    Role = NpcRole.Pirate,
                    EncounterId = MissionRules.FirstSiteId + mission.Id,
                    SpawnSlot = slot,
                    HomeX = p.X,
                    HomeY = p.Y,
                    HomeZ = p.Z,
                    X = p.X,
                    Y = p.Y,
                    Z = p.Z,
                    ShieldCharge = npcProfile.Attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                    UpdatedAt = now,
                    MotionAt = now,
                    AcquisitionRange = placements[slot].AcquisitionRange,
                }
            );
            world.Add(
                new SavedMissionMember
                {
                    NpcId = id,
                    MissionId = mission.Id,
                    Wave = wave,
                    Trigger = group.Trigger,
                    CompletesMission = group.CompletesMission,
                    Required = group.Required,
                    LootTypeId = group.LootTypeId,
                    LootQuantity = group.LootQuantity,
                }
            );
        }
        var progress =
            world.Set<SavedMissionRoom>().Find(mission.Id, roomId)
            ?? world.Add(new SavedMissionRoom { MissionId = mission.Id, RoomId = roomId }).Entity;
        progress.Wave = wave;
        mission.Wave = Math.Max(mission.Wave, wave);
    }

    internal static IReadOnlyList<NpcLootDrop> KeyLoot(WorldAction world, WorldNpc npc, long now)
    {
        if (npc.AnchorKind != NpcAnchorKind.Mission)
        {
            return [];
        }
        var mission = world.Set<SavedMission>().Find(npc.EncounterId - MissionRules.FirstSiteId);
        if (mission is null || mission.State != MissionState.Accepted || mission.ExpiresAt <= now)
        {
            return [];
        }
        if (npc.Role == NpcRole.MissionStructure)
        {
            return [];
        }
        var member = world.Set<SavedMissionMember>().Find(npc.Id);
        return member is { LootTypeId: > 0, LootQuantity: > 0 } ? [new(member.LootTypeId, member.LootQuantity)] : [];
    }
}
