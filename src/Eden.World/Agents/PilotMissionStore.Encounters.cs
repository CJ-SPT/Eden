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
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Agents;

internal sealed record MissionSitesView(IReadOnlyList<MissionSite> Sites, long ExpiresAt);

internal sealed record MissionPreparedSpawn(MissionSpawnGroup Group, MissionSpawnMember Member, NpcHullProfile Profile);

internal sealed partial class PilotMissionStore
{
    public IReadOnlyList<MissionSite> Sites(int systemId)
    {
        return LiveReadModels.Find(database.Path) is { } resident
            ? resident.MissionSites(systemId, Now, () => ReadSites(systemId)).Sites
            : ReadSites(systemId).Sites;
    }

    private MissionSitesView ReadSites(int systemId)
    {
        return LiveWorld.Execute(database.Path, () => ReadSitesOwned(systemId), nameof(ReadSites));
    }

    private MissionSitesView ReadSitesOwned(int systemId)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            var rows = world.Set<SavedMission>().Where(m => m.SystemId == systemId && m.State == MissionState.Accepted).ToArray();
            foreach (var row in rows)
            {
                Expire(world, row, Now);
            }
            var sites = rows.Where(m => m.State == MissionState.Accepted)
                .Where(m => MissionCatalog.Find(catalog, m.MissionId).Kind == MissionKind.Combat)
                .SelectMany(m =>
                {
                    var profile = MissionCatalog.Find(catalog, m.MissionId);
                    var position = new SpacePosition(m.X, m.Y, m.Z);
                    var progress = world.Set<SavedMissionRoom>().Where(r => r.MissionId == m.Id).ToDictionary(r => r.RoomId);
                    var indices = profile
                        .Rooms.SelectMany(r => r.Objects)
                        .Select((o, index) => KeyValuePair.Create(o.Id, index))
                        .ToDictionary();
                    var structures = world.Set<SavedMissionStructure>().Where(s => s.MissionId == m.Id).ToDictionary(s => s.ObjectId);
                    return profile
                        .Rooms.Select(room =>
                        {
                            var objects = room
                                .Objects.Where(o => !profile.Definition.Structures.Any(s => s.ObjectId == o.Id))
                                .Select(o =>
                                    o with
                                    {
                                        Id = SceneObjectId(profile, m.Id, o.Id, indices[o.Id]),
                                        Position = position + o.Position,
                                    }
                                )
                                .Concat(
                                    profile
                                        .Definition.Structures.Select((definition, index) => KeyValuePair.Create(index, definition))
                                        .Where(s =>
                                            s.Value.Drop is not null
                                            && room.Objects.Any(o => o.Id == s.Value.ObjectId)
                                            && structures.GetValueOrDefault(s.Value.ObjectId)?.DestroyedAt is not null
                                        )
                                        .Select(s => RecoveryContainer(catalog, m, profile, s.Key, structures[s.Value.ObjectId].LootEmpty))
                                )
                                .ToArray();
                            var gates = room
                                .Gates.Select(g =>
                                {
                                    long id = SceneObjectId(profile, m.Id, g.ObjectId, indices[g.ObjectId]);
                                    var item = objects.Single(o => o.Id == id);
                                    var destination = position + profile.Rooms.Single(r => r.Id == g.DestinationRoomId).Position;
                                    return new MissionGate(item.Id, item.Position, item.Radius, destination, g.RequiresClear)
                                    {
                                        Access = g.Access,
                                        DungeonObjectId = g.ObjectId,
                                        SourceRoomId = room.Id,
                                    };
                                })
                                .ToArray();
                            bool started =
                                m.ObjectiveComplete
                                || !profile.Definition.Waves.Any((w) => w.RoomId == room.Id || w.RoomId == 0)
                                || progress.ContainsKey(room.Id)
                                || profile.Rooms.Length == 1 && m.Wave >= 0;
                            return new MissionSite(m.Id, m.PilotId, m.SystemId, position + room.Position, started, objects)
                            {
                                RoomId = room.Id,
                                Entrance = position,
                                Gates = gates,
                                Deadspace = profile.Definition.Deadspace,
                            };
                        })
                        .ToArray();
                })
                .ToArray();
            world.SaveChanges();
            transaction.Commit();
            return new(
                sites,
                rows.Where(m => m.State == MissionState.Accepted).Select(m => m.ExpiresAt).DefaultIfEmpty(long.MaxValue).Min()
            );
        }
    }

    public void Arrive(int systemId, SpacePosition position)
    {
        LiveWorld.Execute(database.Path, systemId, () => ArriveOwned(systemId, position), nameof(Arrive));
    }

    private void ArriveOwned(int systemId, SpacePosition position)
    {
        var pending = Sites(systemId).Where(s => !s.Started && s.Position.DistanceTo(position) <= MissionRules.ArrivalRange).ToArray();
        if (pending.Length == 0)
        {
            return;
        }
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            foreach (var site in pending)
            {
                var row = world.Set<SavedMission>().Find(site.Id)!;
                if (row.State == MissionState.Accepted && !row.ObjectiveComplete && !Expire(world, row, Now))
                {
                    var profile = MissionCatalog.Find(catalog, row.MissionId);
                    if (world.Set<SavedMissionRoom>().Find(row.Id, site.RoomId) is null && !(profile.Rooms.Length == 1 && row.Wave >= 0))
                    {
                        int wave = Enumerable.Range(0, profile.Definition.Waves.Length).First(w => profile.WaveRoom(w) == site.RoomId);
                        Spawn(world, catalog, row, wave, Now);
                    }
                }
            }
            world.SaveChanges();
            transaction.Commit();
        }
    }

    internal static void Killed(WorldDbContext world, CatalogDbContext catalog, WorldNpc npc, long now)
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
                    && alive.Contains(m.NpcId)
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

    private static void Spawn(WorldDbContext world, CatalogDbContext catalog, SavedMission mission, int wave, long now)
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

    internal static IReadOnlyList<NpcLootDrop> KeyLoot(WorldDbContext world, WorldNpc npc, long now)
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
