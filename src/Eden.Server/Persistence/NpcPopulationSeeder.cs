using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Mining;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class WorldNpcEncounter
{
    public long Id { get; set; }
    public int SystemId { get; set; }
    public int AnchorId { get; set; }
    public NpcAnchorKind AnchorKind { get; set; }
    public NpcRole Role { get; set; }
    public int FactionId { get; set; }
    public double HomeX { get; set; }
    public double HomeY { get; set; }
    public double HomeZ { get; set; }
    public bool Occupied { get; set; }
    public int Generation { get; set; }
    public int Version { get; set; }
    public long? NextSpawnAt { get; set; }
}

[Injectable(InjectionType.Singleton)]
internal sealed class NpcPopulationSeeder(NpcSpawnCatalog profiles, NpcGuardSeeder guards) : INpcPopulationSeeder
{
    private readonly HashSet<string> _diagnostics = [];

    private void Report(string message)
    {
        lock (_diagnostics)
        {
            if (_diagnostics.Add(message))
            {
                Console.Error.WriteLine("NPC population: " + message);
            }
        }
    }

    public void Seed(WorldDbContext world, CatalogDbContext catalog, int systemId, long now)
    {
        var excluded = NpcSpecialTypes.Excluded.ToArray();
        var retired = world
            .Set<WorldNpc>()
            .Where(n =>
                n.SystemId == systemId
                && n.Role == NpcRole.Pirate
                && excluded.Contains(n.TypeId)
                && n.DestroyedAt == null
                && n.RetiredAt == null
            )
            .ToArray();
        foreach (var npc in retired)
        {
            npc.RetiredAt = now;
            world.Set<NpcAggression>().Where(a => a.NpcId == npc.Id).ExecuteDelete();
            world
                .Set<WorldMissile>()
                .Where(m => m.SourceKind == MissileSourceKind.Npc && m.SourceShipId == npc.Id && m.Status == MissileFlightStatus.Flying)
                .ExecuteUpdate(set => set.SetProperty(m => m.Status, MissileFlightStatus.Expired).SetProperty(m => m.EndedAt, now));
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
        world.Database.ExecuteSql(
            $"""
            UPDATE WorldNpc SET RetiredAt={now}
                WHERE SystemId={systemId} AND TypeId={rookie} AND DestroyedAt IS NULL AND RetiredAt IS NULL
            """
        );
        var system = catalog.Set<SolarSystem>().Include(s => s.Constellation).SingleOrDefault(s => s.Id == systemId);
        if (system is null)
        {
            Report($"system {systemId} has no catalog definition; no population was substituted.");
            return;
        }
        var faction = NpcRegions.Pirates(system.Constellation.RegionId);
        if (faction is null)
        {
            return;
        }
        var definition = catalog
            .Set<SourceRecord>()
            .Find("client/systems", systemId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientSystems>();
        if (definition?.SecurityStatus is not double security)
        {
            Report($"system {systemId} has no pinned security metadata; no population was substituted.");
            return;
        }
        var band = NpcSpawnRules.SecurityBand(security);
        guards.Seed(world, catalog, systemId, definition, band, now);
        var belts = catalog
            .Set<Celestial>()
            .Where(c => c.SolarSystemId == systemId && c.GroupId == AsteroidDefinitions.BeltGroup)
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
            || encounter.Role != NpcRole.Pirate
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
        encounter.NextSpawnAt = checked(now + NpcEncounterDefinitions.RespawnSeconds * TimeSpan.TicksPerSecond);
    }
}
