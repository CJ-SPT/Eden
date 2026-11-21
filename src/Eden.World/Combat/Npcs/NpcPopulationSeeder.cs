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
    internal void WarmCatalog(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var policy = database.Settings.NpcPopulation;
        _ = catalog.Definitions.SystemPolitics;
        if (policy.Pirates)
        {
            _ = profiles.Profiles;
            _ = catalog.BeltAnchors(0);
        }
        if (policy.Guards)
        {
            NpcGuardSeeder.WarmCatalog(catalog);
        }
        if (policy.Convoys)
        {
            convoys.WarmCatalog();
        }
        if (policy.Mining)
        {
            mining.WarmCatalog(catalog);
        }
    }

    internal static void WarmSystem(WorldDatabase database, CatalogDbContext catalog, int systemId)
    {
        if (catalog.FindSystem(systemId) is not { } system || NpcRegions.Pirates(system.RegionId) is null)
        {
            return;
        }
        var politics = catalog.SystemPolitics(systemId);
        if (politics.Security is not double security)
        {
            return;
        }
        var policy = database.Settings.NpcPopulation;
        if (policy.Guards && NpcSpawnRules.SecurityBand(security) != NpcSecurityBand.Null)
        {
            NpcGuardSeeder.WarmSystem(catalog, systemId);
        }
        if (policy.Mining && politics.FactionId.HasValue && NpcConvoyRules.Eligible(security))
        {
            _ = catalog.ReadMany(
                CatalogData.Client.Asteroidcountbytypeidbeltid,
                catalog.BeltAnchors(systemId).Select(belt => (long)belt.Id)
            );
        }
    }

    public long NextUpdate(WorldAction world, int systemId, long now) => NpcPopulationSchedule.Next(world, systemId, now);

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

    public void Seed(WorldAction world, CatalogDbContext catalog, int systemId, long now)
    {
        int rookie = NpcEncounterDefinitions.GuristasRookieType;
        var legacy = NpcPopulationMember
            .InSystem(world, systemId)
            .Where(n => n.Active && (n.TypeId == rookie || n.Role == NpcRole.Pirate && NpcSpecialTypes.Excluded.Contains(n.TypeId)))
            .ToArray();
        var retired = legacy
            .Where(n => n.Role == NpcRole.Pirate && NpcSpecialTypes.Excluded.Contains(n.TypeId))
            .Select(n => world.Set<WorldNpc>().Find(n.Id)!)
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
        if (retired.Length != 0)
        {
            world.SaveChanges();
        }
        foreach (long encounterId in retired.Select(n => n.EncounterId).Distinct())
        {
            if (
                !NpcPopulationMember.AtAnchor(world, encounterId).Any(n => n.Active)
                && world.Set<WorldNpcEncounter>().Find(encounterId) is { } encounter
            )
            {
                encounter.NextSpawnAt = now;
            }
        }
        // Production policy retires the old fixture on activation. Tests inject
        // their own seeder, keeping that fixture isolated from normal space.
        foreach (var member in legacy.Where(n => n.TypeId == rookie))
        {
            world.Set<WorldNpc>().Find(member.Id)!.RetiredAt = now;
        }
        if (legacy.Length != 0)
        {
            world.SaveChanges();
        }
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
        var politics = catalog.SystemPolitics(systemId);
        if (politics.Security is not double security)
        {
            Report($"system {systemId} has no pinned security metadata; no population was substituted.");
            return;
        }
        var band = NpcSpawnRules.SecurityBand(security);
        var policy = world.Settings.NpcPopulation;
        if (policy.Guards)
        {
            guards.Seed(world, catalog, systemId, band, now);
        }
        if (policy.Convoys)
        {
            convoys.Seed(world, catalog, systemId, security, now);
        }
        if (policy.Mining)
        {
            mining.Seed(world, catalog, systemId, politics.FactionId, security, now);
        }
        if (!policy.Pirates)
        {
            return;
        }
        var belts = catalog.BeltAnchors(systemId);
        var encounters = world.Set<WorldNpcEncounter>();
        foreach (var belt in belts)
        {
            if (
                encounters is EconomySet<WorldNpcEncounter, EconomyWorldNpcEncounter> resident
                && resident.ReadSnapshot(new(belt.Id)) is { Version: NpcSpawnRules.PopulationVersion } scheduled
                && (!scheduled.Occupied || scheduled.NextSpawnAt is not long next || now < next)
            )
            {
                continue;
            }
            var encounter = encounters.Find((long)belt.Id);
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
            if (NpcPopulationMember.AtAnchor(world, encounter.Id).Any(n => n.Active))
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
