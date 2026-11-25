using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Mining;
using Eden.World.Runtime.Economy;

namespace Eden.World.Combat.Npcs;

internal static class IncursionRoamingPopulation
{
    internal const long CampInterval = 10 * TimeSpan.TicksPerMinute;

    // Ambient encounters use negative keys beyond the catalog anchors and convoy station keys.
    private const long FirstBeltEncounter = -100_000_000_000_000;
    private const long FirstCampEncounter = -200_000_000_000_000;

    // Incursion hull and corporation IDs from the pinned build 3662168 catalog.
    private const int TruePower = 1000162;
    private const int Schmaeel = 2907;
    private const int Tama = 2939;
    private const int Romi = 2859;
    private const double StandOff = 14_000;
    private const double MemberSpacing = 2_000;
    private static readonly int[] s_belt = [Tama, Romi];
    private static readonly int[] s_ice = [Tama, Romi, Romi];
    private static readonly int[] s_camp = [Schmaeel, Tama, Romi];
    private static readonly NpcProfileCatalog s_hulls = new();
    private static readonly CatalogDefinition<PatrolAnchor[]> s_gates = new(
        "incursion-camp-gates",
        (catalog, system) =>
            catalog
                .GatesInSystem(system)
                .OrderBy(gate => gate.Id)
                .Select(gate => catalog.FindGateBody(gate.Id))
                .OfType<CatalogCelestialDefinition>()
                .Select(gate => new PatrolAnchor(gate.Id, gate.Id, new(gate.X, gate.Y, gate.Z)))
                .ToArray(),
        Estimate: gates => 24 + 48L * gates.Length
    );

    private readonly record struct PatrolAnchor(long Key, int Id, SpacePosition Position);

    internal static bool Reconcile(WorldAction world, CatalogDbContext catalog, int systemId, long now)
    {
        if (world is not EconomyTransaction resident || IncursionStore.NextPopulationOpening(systemId, now) == long.MaxValue)
        {
            return false;
        }
        if (IncursionStore.ActiveFocus(world, catalog, systemId, now) is not { } focus)
        {
            var retiredTales = world
                .Filter<WorldNpcEncounter>(encounter => encounter.SystemId == systemId)
                .AsEnumerable()
                .Where(encounter => NpcPopulationRules.IsIncursionPatrol(encounter.AnchorKind))
                .Select(encounter => (long)encounter.Generation)
                .Distinct()
                .ToArray();
            foreach (long tale in retiredTales)
            {
                RetireSystem(world, systemId, tale, now);
            }
            return false;
        }
        if (
            catalog.FittingType(Tama) is null
            || catalog.FittingType(Romi) is null
            || focus.Security != IncursionSecurity.High && catalog.FittingType(Schmaeel) is null
        )
        {
            return false;
        }
        IncursionStore.Track(world, focus);

        var belts = catalog.BeltAnchors(systemId);
        var nativeAnchors = belts.Select(belt => belt.Id).ToHashSet();
        var displaced = NpcPopulationMember
            .InSystem(world, systemId)
            .Where(npc => IsNativeBeltPirate(npc) && nativeAnchors.Contains(npc.BeltId))
            .ToArray();
        RetireMembers(world, displaced, now);
        foreach (long encounterId in displaced.Select(npc => npc.EncounterId).Distinct())
        {
            if (world.Set<WorldNpcEncounter>().Find(encounterId) is { AnchorKind: NpcAnchorKind.Belt, Role: NpcRole.Pirate } native)
            {
                // Keep the original native generation. Withdrawal rearms the exact roster that warps out here.
                native.NextSpawnAt = focus.EndsAt;
            }
        }

        foreach (var belt in belts)
        {
            SeedBelt(world, catalog, focus, systemId, new(belt.Id, belt.Id, new(belt.X, belt.Y, belt.Z)), s_belt, focus.EndsAt, now);
        }

        MiningSiteStore.EnsureIce(resident, catalog, systemId, now);
        if (LatestIce(world, systemId) is { } ice)
        {
            if (ice.ReplaceAt is not null || ice.ExpiresAt <= now)
            {
                RetireIce(world, ice.Id, now);
            }
            else
            {
                var anchor = new PatrolAnchor(ice.Id, 0, new(ice.X, ice.Y, ice.Z));
                SeedBelt(world, catalog, focus, systemId, anchor, s_ice, ice.ExpiresAt, now);
            }
        }

        if (focus.Security != IncursionSecurity.High)
        {
            SeedCamp(world, catalog, focus, systemId, now);
        }
        return true;
    }

    internal static bool IsNativeBeltPirate(NpcPopulationMember npc) =>
        npc is { Active: true, Role: NpcRole.Pirate, BeltId: > 0 } && NpcPopulationRules.IsNativeBelt(npc.AnchorKind);

    internal static long NextUpdate(WorldAction world, int systemId, long now)
    {
        long opening = IncursionStore.NextPopulationOpening(systemId, now);
        if (world is not EconomyTransaction || !IncursionStore.MayHaveSite(systemId, now))
        {
            return opening;
        }
        long ends = IncursionRules.EndsAt(IncursionRules.TaleId(now));
        var ice = LatestIce(world, systemId);
        long iceDeadline = ice?.ReplaceAt ?? ice?.ExpiresAt ?? long.MaxValue;
        return iceDeadline > now ? Math.Min(ends, iceDeadline) : ends;
    }

    private static EconomySavedMiningSite? LatestIce(WorldAction world, int systemId) =>
        ((EconomySet<SavedMiningSite, EconomySavedMiningSite>)world.Set<SavedMiningSite>())
            .IndexedSnapshots(nameof(SavedMiningSite.SystemId), systemId)
            .Where(site => site.SystemId == systemId && site.Family == MiningFamily.Ice)
            .MaxBy(site => site.Id);

    internal static void RetireIce(WorldAction world, long siteId, long now)
    {
        if (world is EconomyTransaction && world.Set<WorldNpcEncounter>().Find(FirstBeltEncounter - siteId) is { } encounter)
        {
            RetireEncounter(world, encounter, now);
        }
    }

    internal static void Retire(WorldAction world, IReadOnlyList<IncursionSystem> systems, long taleId, long now)
    {
        foreach (var system in systems)
        {
            RetireSystem(world, system.SystemId, taleId, now);
        }
    }

    private static void RetireSystem(WorldAction world, int systemId, long taleId, long now)
    {
        long ends = IncursionRules.EndsAt(taleId);
        var encounters = world.Filter<WorldNpcEncounter>(encounter => encounter.SystemId == systemId).ToArray();
        foreach (var encounter in encounters)
        {
            if (NpcPopulationRules.IsIncursionPatrol(encounter.AnchorKind) && encounter.Generation == taleId)
            {
                RetireEncounter(world, encounter, now);
            }
            else if (encounter is { AnchorKind: NpcAnchorKind.Belt, Role: NpcRole.Pirate } && encounter.NextSpawnAt == ends)
            {
                encounter.NextSpawnAt = now;
            }
        }
    }

    private static void SeedCamp(WorldAction world, CatalogDbContext catalog, IncursionFocus focus, int systemId, long now)
    {
        var gates = catalog.Definition(s_gates, systemId);
        if (gates.Length == 0)
        {
            return;
        }
        long opens = IncursionRules.OpensAt(focus.TaleId);
        long shift = (now - opens) / CampInterval;
        long ends = Math.Min(focus.EndsAt, opens + (shift + 1) * CampInterval);
        long id = FirstCampEncounter - systemId;
        var encounters = world.Set<WorldNpcEncounter>();
        var camp = encounters.Find(id);
        if (camp is { } && camp.Generation == focus.TaleId && camp.NextSpawnAt == ends)
        {
            return;
        }
        if (camp is not null)
        {
            RetireMembers(world, NpcPopulationMember.AtAnchor(world, id).Where(npc => npc.Active).ToArray(), now);
        }
        uint first = NpcSpawnRules.Seed(systemId, checked((int)focus.TaleId));
        var gate = gates[(int)((first + shift) % gates.Length)];
        camp ??= AddEncounter(world, systemId, focus, id, gate, NpcAnchorKind.IncursionCamp);
        (camp.AnchorId, camp.Generation, camp.NextSpawnAt) = (gate.Id, checked((int)focus.TaleId), ends);
        (camp.HomeX, camp.HomeY, camp.HomeZ) = (gate.Position.X, gate.Position.Y, gate.Position.Z);
        Spawn(world, catalog, camp, s_camp, ends, now);
    }

    private static void SeedBelt(
        WorldAction world,
        CatalogDbContext catalog,
        IncursionFocus focus,
        int systemId,
        PatrolAnchor anchor,
        int[] roster,
        long expiresAt,
        long now
    )
    {
        long id = FirstBeltEncounter - anchor.Key;
        var encounter = world.Set<WorldNpcEncounter>().Find(id);
        if (encounter is null)
        {
            encounter = AddEncounter(world, systemId, focus, id, anchor, NpcAnchorKind.IncursionBelt);
            encounter.NextSpawnAt = now;
        }
        else if (encounter.Generation != focus.TaleId)
        {
            RetireMembers(world, NpcPopulationMember.AtAnchor(world, id).Where(npc => npc.Active).ToArray(), now);
            encounter.Generation = checked((int)focus.TaleId);
            encounter.NextSpawnAt = now;
        }
        if (encounter.NextSpawnAt <= now)
        {
            Spawn(world, catalog, encounter, roster, Math.Min(focus.EndsAt, expiresAt), now);
            encounter.NextSpawnAt = null;
        }
    }

    private static WorldNpcEncounter AddEncounter(
        WorldAction world,
        int systemId,
        IncursionFocus focus,
        long id,
        PatrolAnchor anchor,
        NpcAnchorKind kind
    )
    {
        WorldNpcEncounter encounter = new()
        {
            Id = id,
            SystemId = systemId,
            AnchorId = anchor.Id,
            AnchorKind = kind,
            Role = NpcRole.Pirate,
            FactionId = (int)NpcPirateFaction.Sansha,
            HomeX = anchor.Position.X,
            HomeY = anchor.Position.Y,
            HomeZ = anchor.Position.Z,
            Occupied = true,
            // RecordKill keeps the ambient campaign generation until withdrawal.
            Generation = checked((int)focus.TaleId),
            Version = NpcSpawnRules.PopulationVersion,
        };
        world.Add(encounter);
        return encounter;
    }

    private static void Spawn(WorldAction world, CatalogDbContext catalog, WorldNpcEncounter encounter, int[] roster, long ends, long now)
    {
        for (int slot = 0; slot < roster.Length; slot++)
        {
            int typeId = roster[slot];
            var hull = s_hulls.Read(catalog, typeId);
            double x = encounter.HomeX + StandOff;
            double y = encounter.HomeY + slot * MemberSpacing;
            world.Add(
                new WorldNpc
                {
                    BeltId = encounter.AnchorId,
                    EncounterId = encounter.Id,
                    SpawnSlot = slot,
                    SystemId = encounter.SystemId,
                    TypeId = typeId,
                    Role = NpcRole.Pirate,
                    AnchorKind = encounter.AnchorKind,
                    FactionId = encounter.FactionId,
                    CorporationId = TruePower,
                    HomeX = x,
                    HomeY = y,
                    HomeZ = encounter.HomeZ,
                    X = x,
                    Y = y,
                    Z = encounter.HomeZ,
                    ShieldCharge = hull.Attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                    ExpiresAt = ends,
                    UpdatedAt = now,
                }
            );
        }
    }

    private static void RetireEncounter(WorldAction world, WorldNpcEncounter encounter, long now)
    {
        RetireMembers(world, NpcPopulationMember.AtAnchor(world, encounter.Id).Where(npc => npc.Active).ToArray(), now);
        world.Remove(encounter);
    }

    private static void RetireMembers(WorldAction world, NpcPopulationMember[] members, long now)
    {
        foreach (var member in members)
        {
            if (world.Set<WorldNpc>().Find(member.Id) is not { RetiredAt: null, DestroyedAt: null } npc)
            {
                continue;
            }
            npc.RetiredAt = now;
            if (world.Set<NpcAggression>().Find(npc.Id) is { } aggression)
            {
                world.Remove(aggression);
            }
            world.RemoveRange(world.Filter<WorldNpcEwar>(effect => effect.NpcId == npc.Id));
            world.RemoveRange(world.Filter<SavedCombatDamage>(damage => damage.VictimId == npc.Id));
            foreach (
                var missile in world.Filter<WorldMissile>(row => row.SourceShipId == npc.Id && row.SourceKind == MissileSourceKind.Npc)
            )
            {
                if (missile.Status == MissileFlightStatus.Flying)
                {
                    missile.Status = MissileFlightStatus.Expired;
                    missile.EndedAt = now;
                }
            }
        }
    }
}
