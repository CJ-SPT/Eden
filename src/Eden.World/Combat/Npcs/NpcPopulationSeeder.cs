using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

[Injectable(InjectionType.Singleton)]
internal sealed partial class NpcPopulationSeeder(
    NpcSpawnCatalog profiles,
    NpcGuardSeeder guards,
    NpcConvoySeeder convoys,
    NpcMiningSeeder mining
) : INpcPopulationSeeder
{
    public long NextUpdate(WorldDbContext world, int systemId, long now) => NpcPopulationSchedule.Next(world, systemId, now);

    private readonly HashSet<string> _diagnostics = [];

    private void Report(string message)
    {
        lock (_diagnostics)
        {
            if (_diagnostics.Add(message))
            {
                ServerLog.Console.Warning("NPC population: " + message);
            }
        }
    }

    public void Seed(WorldDbContext world, CatalogDbContext catalog, int systemId, long now) =>
        Seed(new SqlWorldAction(world), catalog, systemId, now);

    internal void Seed(WorldAction world, CatalogDbContext catalog, int systemId, long now)
    {
        var excluded = NpcSpecialTypes.Excluded.ToArray();
        var retired = world
            .Filter<WorldNpc>(n =>
                n.SystemId == systemId
                && n.Role == NpcRole.Pirate
                && Enumerable.Contains(excluded, n.TypeId)
                && n.DestroyedAt == null
                && n.RetiredAt == null
            )
            .ToArray();
        foreach (var npc in retired)
        {
            npc.RetiredAt = now;
            if (world.Set<NpcAggression>().Find(npc.Id) is { } aggression)
            {
                world.Remove(aggression);
            }
            foreach (
                var missile in world.Filter<WorldMissile>(m =>
                    m.SourceKind == MissileSourceKind.Npc && m.SourceShipId == npc.Id && m.Status == MissileFlightStatus.Flying
                )
            )
            {
                missile.Status = MissileFlightStatus.Expired;
                missile.EndedAt = now;
            }
        }
        world.SaveChanges();
        foreach (long encounterId in retired.Select(n => n.EncounterId).Distinct())
        {
            if (
                !world.Set<WorldNpc>().Any(n => n.EncounterId == encounterId && n.DestroyedAt == null && n.RetiredAt == null)
                && world.Set<WorldNpcEncounter>().Find(encounterId) is { } encounter
            )
            {
                encounter.NextSpawnAt = now;
            }
        }
        // Production policy retires the old fixture on activation. Tests inject
        // their own seeder, keeping that fixture isolated from normal space.
        int rookie = NpcEncounterDefinitions.GuristasRookieType;
        foreach (
            var npc in world.Filter<WorldNpc>(n =>
                n.SystemId == systemId && n.TypeId == rookie && n.DestroyedAt == null && n.RetiredAt == null
            )
        )
        {
            npc.RetiredAt = now;
        }
        world.SaveChanges();
        var system = catalog.FindSystem(systemId);
        if (system is null)
        {
            Report($"system {systemId} has no catalog definition; no population was substituted.");
            return;
        }
        var faction = NpcRegions.Pirates(system.RegionId);
        if (faction is null)
        {
            return;
        }
        var definition = catalog.Read(CatalogData.Client.Systems, (long)systemId);
        if (definition?.SecurityStatus is not double security)
        {
            Report($"system {systemId} has no pinned security metadata; no population was substituted.");
            return;
        }
        var band = NpcSpawnRules.SecurityBand(security);
        var policy = world.Settings.NpcPopulation;
        if (policy.Guards)
        {
            guards.Seed(world, catalog, systemId, definition, band, now);
        }
        if (policy.Convoys)
        {
            convoys.Seed(world, catalog, systemId, security, now);
        }
        if (policy.Mining)
        {
            mining.Seed(world, catalog, systemId, definition, security, now);
        }
        if (!policy.Pirates)
        {
            return;
        }
        var belts = catalog
            .CelestialsInSystem(systemId)
            .Where(c => c.GroupId == AsteroidDefinitions.BeltGroup)
            .OrderBy(c => c.Id)
            .ToArray();
        foreach (var belt in belts)
        {
            var encounter = world.Set<WorldNpcEncounter>().Find((long)belt.Id);
            if (encounter is null)
            {
                encounter = new()
                {
                    Id = belt.Id,
                    SystemId = systemId,
                    AnchorId = belt.Id,
                    AnchorKind = NpcAnchorKind.Belt,
                    Role = NpcRole.Pirate,
                    FactionId = (int)faction.Value,
                    HomeX = belt.X + NpcSpawnRules.BeltStandOff,
                    HomeY = belt.Y,
                    HomeZ = belt.Z,
                    Occupied = band != NpcSecurityBand.Safest && NpcSpawnRules.Occupied(NpcSpawnRules.Seed(systemId, belt.Id)),
                    Version = NpcSpawnRules.PopulationVersion,
                    NextSpawnAt = now,
                };
                world.Add(encounter);
            }
            encounter.Version = NpcSpawnRules.PopulationVersion;
            if (!encounter.Occupied || encounter.NextSpawnAt is not long deadline || now < deadline)
            {
                continue;
            }
            if (world.Set<WorldNpc>().Any(n => n.EncounterId == encounter.Id && n.DestroyedAt == null && n.RetiredAt == null))
            {
                continue;
            }
            uint seed = NpcSpawnRules.Seed(systemId, belt.Id, encounter.Generation);
            var composition = NpcSpawnRules.Composition(band, seed);
            List<NpcSpawnProfile> members = [];
            foreach (var hull in composition)
            {
                var pool = profiles.Pool(faction.Value, hull);
                if (pool.Count == 0)
                {
                    Report($"no valid {faction.Value} {hull} profiles for system {systemId}; encounter {belt.Id} skipped.");
                    break;
                }
                uint choice = NpcSpawnRules.Seed(systemId, belt.Id + members.Count, encounter.Generation);
                members.Add(pool[(int)(choice % (uint)pool.Count)]);
            }
            if (members.Count != composition.Count)
            {
                continue;
            }
            double offset = 0;
            foreach (var member in members)
            {
                world.Add(
                    new WorldNpc
                    {
                        BeltId = belt.Id,
                        EncounterId = encounter.Id,
                        SystemId = systemId,
                        TypeId = member.TypeId,
                        Role = NpcRole.Pirate,
                        FactionId = (int)member.Faction,
                        CorporationId = member.CorporationId,
                        HomeX = encounter.HomeX,
                        HomeY = encounter.HomeY,
                        HomeZ = encounter.HomeZ + offset,
                        X = encounter.HomeX,
                        Y = encounter.HomeY,
                        Z = encounter.HomeZ + offset,
                        ShieldCharge = member.Attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                        UpdatedAt = now,
                    }
                );
                offset += NpcSpawnRules.MemberSpacing + member.Radius * 2;
            }
            encounter.NextSpawnAt = null;
        }
    }

    internal static void RecordKill(WorldDbContext world, WorldNpc npc, long now)
    {
        var encounter = world.Set<WorldNpcEncounter>().Find(npc.EncounterId);
        if (
            encounter is null
            || encounter.Role is not (NpcRole.Pirate or NpcRole.Convoy)
            || world
                .Set<WorldNpc>()
                .Where(n => n.EncounterId == encounter.Id && n.Id != npc.Id && n.DestroyedAt == null && n.RetiredAt == null)
                .ToArray()
                .Any(n => n.DestroyedAt == null && n.RetiredAt == null)
        )
        {
            return;
        }
        encounter.Generation = checked(encounter.Generation + 1);
        int delay = encounter.Role == NpcRole.Convoy ? NpcConvoyRules.CooldownSeconds : NpcEncounterDefinitions.RespawnSeconds;
        encounter.NextSpawnAt = checked(now + delay * TimeSpan.TicksPerSecond);
    }
}
