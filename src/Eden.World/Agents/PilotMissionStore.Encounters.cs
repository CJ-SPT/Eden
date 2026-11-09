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
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        lock (database.MotionLock)
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            var rows = world.Set<SavedMission>().Where(m => m.SystemId == systemId && m.State == MissionState.Accepted).ToArray();
            foreach (var row in rows)
            {
                Expire(world, row, Now);
            }
            var sites = rows.Where(m => m.State == MissionState.Accepted && !m.ObjectiveComplete)
                .Where(m => MissionCatalog.Find(catalog, m.MissionId).Kind == MissionKind.Combat)
                .Select(m =>
                {
                    var profile = MissionCatalog.Find(catalog, m.MissionId);
                    var position = new SpacePosition(m.X, m.Y, m.Z);
                    var objects = profile
                        .Objects.Select(
                            (o, i) =>
                                o with
                                {
                                    Id = checked(MissionRules.FirstObjectId + m.Id * MissionRules.NpcsPerMission + i),
                                    Position = position + o.Position,
                                }
                        )
                        .ToArray();
                    return new MissionSite(m.Id, m.PilotId, m.SystemId, position, m.Wave >= 0, objects);
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
        var pending = Sites(systemId).Where(s => !s.Started && s.Position.DistanceTo(position) <= MissionRules.ArrivalRange).ToArray();
        if (pending.Length == 0)
        {
            return;
        }
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        lock (database.MotionLock)
        {
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            foreach (var site in pending)
            {
                var row = world.Set<SavedMission>().Find(site.Id)!;
                if (row.State == MissionState.Accepted && row.Wave < 0 && !Expire(world, row, Now))
                {
                    Spawn(world, catalog, row, 0, Now);
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
        if (mission.State != MissionState.Accepted || mission.ObjectiveComplete || Expire(world, mission, now))
        {
            return;
        }
        var member = world.Set<SavedMissionMember>().Find(npc.Id)!;
        if (member.CompletesMission)
        {
            mission.ObjectiveComplete = true;
            return;
        }
        var members = world.Set<SavedMissionMember>().Where(m => m.MissionId == mission.Id).ToArray();
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
        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        if (member.Wave == mission.Wave && mission.Wave + 1 < profile.Definition.Waves.Length)
        {
            var waveMembers = members.Where(m => m.Wave == mission.Wave).ToArray();
            bool triggers = waveMembers.Any(m => m.Trigger);
            if (!waveMembers.Any(m => (triggers ? m.Trigger : m.Required) && alive.Contains(m.NpcId)))
            {
                Spawn(world, catalog, mission, mission.Wave + 1, now);
                return;
            }
        }
        if (mission.Wave + 1 == profile.Definition.Waves.Length && !members.Any(m => m.Required && alive.Contains(m.NpcId)))
        {
            mission.ObjectiveComplete = true;
        }
    }

    private static void Spawn(WorldDbContext world, CatalogDbContext catalog, SavedMission mission, int wave, long now)
    {
        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        int slot = 0;
        foreach (var group in profile.Definition.Waves[wave].Groups)
        {
            for (int i = 0; i < group.Count; i++)
            {
                if (slot >= MissionRules.MembersPerWave)
                {
                    throw new InvalidDataException("Mission wave exceeds its reserved NPC identity range.");
                }
                uint seed = NpcSpawnRules.Seed(mission.SystemId, wave, checked((int)mission.Id + slot));
                int type = group.Types[(int)(seed % group.Types.Length)];
                var npcProfile = new NpcProfileCatalog().Read(catalog, type);
                long id = checked(
                    MissionRules.FirstNpcId + mission.Id * MissionRules.NpcsPerMission + wave * MissionRules.MembersPerWave + slot
                );
                var p = new SpacePosition(mission.X + group.Range, mission.Y + slot * NpcSpawnRules.MemberSpacing, mission.Z);
                world.Add(
                    new WorldNpc
                    {
                        Id = id,
                        TypeId = type,
                        SystemId = mission.SystemId,
                        CorporationId = profile.EnemyCorporation,
                        FactionId = profile.EnemyFaction,
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
                        AcquisitionRange = NpcEncounterDefinitions.VisibilityRange,
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
                    }
                );
                slot++;
            }
        }
        mission.Wave = wave;
    }
}
