using System.Collections.Concurrent;
using System.Collections.Immutable;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal sealed partial class CombatAnomalyStore
{
    private static void Invalidate(WorldAction world, int system) =>
        world.AfterCommit(() => s_versions.AddOrUpdate((world.WorldPath, system), 1, (_, old) => old + 1));

    internal static void Killed(WorldAction world, CatalogDbContext catalog, WorldNpc npc, long now)
    {
        if (npc.AnchorKind != NpcAnchorKind.Anomaly)
        {
            return;
        }
        var site = world.Set<WorldCombatAnomaly>().Find(npc.EncounterId) ?? throw new InvalidDataException("Anomaly NPC has no site.");
        if (site.CompletedAt is not null)
        {
            return;
        }
        if (site.Slot == DedComplexRules.SiteSlot)
        {
            if (DedKilled(world, catalog, site, npc, now))
            {
                Invalidate(world, site.SystemId);
                Invalidate(world, -1);
            }
            return;
        }
        var alive = world
            .Set<WorldNpc>()
            .Where(n => n.EncounterId == site.Id && n.Id != npc.Id && n.DestroyedAt == null && n.RetiredAt == null)
            .ToArray()
            .Where(n => n.DestroyedAt is null && n.RetiredAt is null)
            .ToArray();
        var definition =
            CombatAnomalyCatalog.Find(catalog, site.DungeonId)
            ?? throw new InvalidDataException($"Anomaly definition {site.DungeonId} is missing.");
        var member = world.Set<WorldAnomalyMember>().Find(npc.Id);
        if (site.DefinitionVersion == 0)
        {
            if (alive.Length == 0)
            {
                NextWave(world, catalog, site, now);
            }
        }
        else if (member?.Wave == site.Wave)
        {
            var members = world.Set<WorldAnomalyMember>().Where(m => m.SiteId == site.Id && m.Wave == site.Wave).ToArray();
            bool triggers = members.Any(m => m.Trigger);
            var blocking = members.Where(m => triggers ? m.Trigger : !m.Sentry).Select(m => m.NpcId).ToHashSet();
            if (!site.NextWaveAt.HasValue && !alive.Any(n => Enumerable.Contains(blocking, n.Id)))
            {
                NextWave(world, catalog, site, now);
            }
        }
        if (
            site.Wave >= definition.WavesFor(site.DefinitionVersion).Length - 1
            && alive.Length == 0
            && !world.Set<WorldNpc>().Local.Any(n => n.EncounterId == site.Id && n.DestroyedAt is null && n.RetiredAt is null)
        )
        {
            Complete(site, now);
        }
        Invalidate(world, site.SystemId);
        if (site.CompletedAt.HasValue)
        {
            Invalidate(world, -1);
        }
    }

    private static void NextWave(WorldAction world, CatalogDbContext catalog, WorldCombatAnomaly site, long now)
    {
        if (site.DefinitionVersion == 0)
        {
            if (site.Wave == 3)
            {
                Complete(site, now);
                return;
            }
            site.Wave++;
            var legacy = CombatAnomalyRules.Wave(site.Id, site.Wave).Select(id => new AnomalySpawnMember(id, 0, false, false)).ToArray();
            Spawn(world, catalog, site, legacy, now);
            return;
        }
        var definition = CombatAnomalyCatalog.Find(catalog, site.DungeonId)!;
        if (site.Wave < 0)
        {
            // Unvisited sites adopt corrections; started version-one sites retain
            // their original recipe so saved wave indices and deadlines stay valid.
            site.DefinitionVersion = AnomalyEncounterRules.CurrentDefinitionVersion;
        }
        var waves = definition.WavesFor(site.DefinitionVersion);
        while (site.Wave + 1 < waves.Length)
        {
            site.Wave++;
            var members = AnomalyEncounterRules.Members(site.Id, site.Wave, waves[site.Wave]);
            if (members.Count > 0)
            {
                Spawn(world, catalog, site, members, now);
                if (site.Wave + 1 < waves.Length && waves[site.Wave + 1].DelaySeconds > 0)
                {
                    site.NextWaveAt = checked(now + waves[site.Wave + 1].DelaySeconds * TimeSpan.TicksPerSecond);
                }
                return;
            }
        }
        // Reinforcements may have arrived before earlier hostiles were cleared.
        bool remaining = world
            .Set<WorldNpc>()
            .Where(n => n.EncounterId == site.Id && n.DestroyedAt == null && n.RetiredAt == null)
            .ToArray()
            .Any(n => n.DestroyedAt is null && n.RetiredAt is null);
        if (!remaining)
        {
            Complete(site, now);
        }
    }

    private static void Spawn(
        WorldAction world,
        CatalogDbContext catalog,
        WorldCombatAnomaly site,
        IReadOnlyList<AnomalySpawnMember> members,
        long now
    )
    {
        var definition = CombatAnomalyCatalog.Find(catalog, site.DungeonId)!;
        var origin = new SpacePosition(site.X, site.Y, site.Z) + definition.Spawner;
        var placements =
            site.DefinitionVersion == 0
                ? []
                : AnomalySpawnRules.Place(
                    site.Wave,
                    members,
                    definition.SpawnAnchors,
                    new(definition.Spawner, definition.AcquisitionRange)
                );
        for (int slot = 0; slot < members.Count; slot++)
        {
            var member = members[slot];
            var profile = new NpcProfileCatalog().Read(catalog, member.TypeId);
            var p =
                site.DefinitionVersion == 0
                    ? origin + new SpacePosition((slot % 8) * NpcSpawnRules.MemberSpacing, 0, (slot / 8) * NpcSpawnRules.MemberSpacing)
                    : new SpacePosition(site.X, site.Y, site.Z) + placements[slot].Position;
            long id = checked(
                CombatAnomalyRules.FirstNpcId
                + (site.Id - CombatAnomalyRules.FirstSiteId) * AnomalyEncounterRules.NpcsPerSite
                + site.Wave * AnomalyEncounterRules.MembersPerWave
                + slot
            );
            world.Add(
                new WorldNpc
                {
                    Id = id,
                    SystemId = site.SystemId,
                    TypeId = member.TypeId,
                    Role = NpcRole.Pirate,
                    AnchorKind = NpcAnchorKind.Anomaly,
                    EncounterId = site.Id,
                    SpawnSlot = slot,
                    AcquisitionRange = site.DefinitionVersion == 0 ? definition.AcquisitionRange : placements[slot].AcquisitionRange,
                    CorporationId = definition.CorporationId,
                    FactionId = (int)definition.Faction,
                    HomeX = p.X,
                    HomeY = p.Y,
                    HomeZ = p.Z,
                    X = p.X,
                    Y = p.Y,
                    Z = p.Z,
                    ShieldCharge = profile.Attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                    UpdatedAt = now,
                    MotionAt = now,
                }
            );
            world.Add(
                new WorldAnomalyMember
                {
                    NpcId = id,
                    SiteId = site.Id,
                    Wave = site.Wave,
                    Trigger = member.Trigger,
                    Sentry = member.Sentry,
                }
            );
        }
    }
}
