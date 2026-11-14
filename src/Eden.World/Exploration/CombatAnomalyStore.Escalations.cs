using System.Collections.Concurrent;
using Eden.Data.Catalog;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal sealed partial class CombatAnomalyStore
{
    private sealed record EscalationView(
        long Version,
        long Catalog,
        long ExpiresAt,
        IReadOnlyList<DedEscalation> Journal,
        IReadOnlyList<CombatAnomaly> Sites
    );

    private static readonly ConcurrentDictionary<(string Path, long Pilot), long> s_escalationVersions = [];
    private readonly ConcurrentDictionary<long, EscalationView> _escalationViews = [];

    private static void InvalidateEscalations(string path, long pilot) =>
        s_escalationVersions.AddOrUpdate((path, pilot), 1, (_, version) => version + 1);

    private EscalationView Escalations(long pilot, long now)
    {
        long version = s_escalationVersions.GetValueOrDefault((database.Path, pilot));
        long catalogVersion = database.Catalog.Identity.Generation;
        if (
            _escalationViews.TryGetValue(pilot, out var cached)
            && cached.Version == version
            && cached.Catalog == catalogVersion
            && now < cached.ExpiresAt
        )
        {
            return cached;
        }
        using var owner = LiveWorld.Find(database.Path)?.Enter();
        using var movement = database.EnterMovement();
        using CatalogDbContext catalog = new(database.CatalogPath);
        using WorldActionScope transaction = new(
            database,
            select: world =>
                new(world.Filter<WorldCombatAnomaly>(s => s.EscalationPilotId == pilot && s.CompletedAt == null).Select(s => s.SystemId))
        );
        var world = transaction.World;
        var candidates = world
            .Filter<WorldCombatAnomaly>(s => s.EscalationPilotId == pilot && s.CompletedAt == null)
            .OrderBy(s => s.Id)
            .ToArray();
        List<DedEscalation> journal = [];
        List<CombatAnomaly> sites = [];
        foreach (var site in candidates)
        {
            if (site.EscalationExpiresAt is not long expiry || expiry <= now)
            {
                site.CompletedAt = now;
                RetireDedMembers(world, site, now);
                world.AfterCommit(() => _dedStarted.TryRemove(site.Id, out _));
                continue;
            }
            var definition = DedComplexCatalog.Find(catalog, site.DungeonId);
            var dungeon = catalog.Require(CatalogData.Client.Dungeons, (long)site.DungeonId);
            sites.Add(DedContact(site, definition));
            journal.Add(
                new(
                    site.Id,
                    site.SystemId,
                    site.DungeonId,
                    definition.NameId,
                    checked((int)(dungeon.DescriptionID ?? definition.NameId)),
                    definition.FactionId,
                    definition.Difficulty,
                    site.EscalationSourceDungeonId,
                    expiry
                )
            );
        }
        transaction.Commit();
        var view = new EscalationView(
            version,
            catalogVersion,
            journal.Select(e => e.ExpiresAt).DefaultIfEmpty(long.MaxValue).Min(),
            journal.ToArray(),
            sites.ToArray()
        );
        _escalationViews[pilot] = view;
        return view;
    }

    public IReadOnlyList<DedEscalation> Journal(long pilotId, long now) =>
        LiveWorld.Execute(database.Path, () => Escalations(pilotId, now).Journal, nameof(Journal));

    public CombatAnomaly Find(long pilotId, int systemId, long instanceId, long now) =>
        Sites(pilotId, systemId, now).SingleOrDefault(s => s.Id == instanceId)
        ?? throw new InvalidDataException("This expedition is unavailable in this solar system.");

    public IReadOnlyList<CombatAnomaly> Sites(long pilotId, int systemId, long now) =>
        LiveWorld.Execute(
            database.Path,
            () =>
            {
                var sites = Escalations(pilotId, now).Sites;
                return sites.Count == 0 ? sites : sites.Where(s => s.SystemId == systemId).ToArray();
            },
            nameof(Sites)
        );

    public void Arrive(long pilotId, int systemId, SpacePosition position, long now)
    {
        LiveWorld.Execute(
            database.Path,
            systemId,
            () =>
            {
                using var owner = LiveWorld.Find(database.Path)?.Enter();
                using var movement = database.EnterMovement();
                var sites = Escalations(pilotId, now).Sites;
                if (sites.Count == 0)
                {
                    return;
                }
                using CatalogDbContext catalog = new(database.CatalogPath);
                foreach (var site in sites.Where(s => s.SystemId == systemId))
                {
                    ArriveDed(site, DedComplexCatalog.Find(catalog, site.DungeonId), position, now);
                }
            },
            nameof(Arrive)
        );
    }

    private static void GrantEscalation(
        WorldAction world,
        CatalogDbContext catalog,
        WorldCombatAnomaly source,
        long pilot,
        int targetDungeon,
        long now
    )
    {
        if (
            pilot == 0
            || targetDungeon == 0
            || !world.Settings.NpcPopulation.Pirates
            || DedComplexCatalog.Read(catalog).FirstOrDefault(d => d.Binding.DungeonId == targetDungeon) is not { } definition
        )
        {
            return;
        }
        int bands = definition.Binding.SecurityBands;
        int system = FindEscalationSystem(catalog, source.SystemId, bands, targetDungeon);
        var anchor = catalog.CelestialsInSystem(system).OrderBy(c => c.Id).FirstOrDefault();
        if (anchor is null)
        {
            return;
        }
        long id = NextSiteId(world);
        double offset = (DedComplexRules.EscalationSlot + id - CombatAnomalyRules.FirstSiteId) * CombatAnomalyRules.PlacementOffset;
        world.Add(
            new WorldCombatAnomaly
            {
                Id = id,
                SystemId = system,
                DungeonId = targetDungeon,
                Slot = DedComplexRules.EscalationSlot,
                EscalationPilotId = pilot,
                EscalationSourceDungeonId = source.DungeonId,
                EscalationExpiresAt = checked(now + DedEscalationRules.Lifetime(targetDungeon)),
                X = anchor.X + offset,
                Y = anchor.Y + offset,
                Z = anchor.Z - offset,
            }
        );
        world.AfterCommit(() => InvalidateEscalations(world.WorldPath, pilot));
    }

    private static int FindEscalationSystem(CatalogDbContext catalog, int origin, int bands, int dungeon)
    {
        // Rated 5/10 rewards can appear in highsec through an escalation.
        if (bands == DedComplexRules.LowSecurity)
        {
            bands |= DedComplexRules.HighSecurity;
        }
        if (dungeon is DedComplexRules.NullDroneHive or DedEscalationRules.DroneInfestedMine)
        {
            bands = DedComplexRules.NullSecurity;
        }
        HashSet<int> visited = [origin];
        int[] frontier = [origin];
        for (int jump = 0; jump < DedEscalationRules.DestinationSearchJumps && frontier.Length > 0; jump++)
        {
            frontier = frontier
                .SelectMany(catalog.GatesInSystem)
                .Select(g => g.DestinationSystemId)
                .Distinct()
                .Order()
                .Where(visited.Add)
                .ToArray();
            foreach (int system in frontier)
            {
                if (
                    (
                        bands == 0
                        || catalog.Read(CatalogData.Client.Systems, (long)system)?.SecurityStatus is double security
                            && (bands & DedComplexRules.SecurityBand(security)) != 0
                    ) && catalog.CelestialsInSystem(system).Any()
                )
                {
                    return system;
                }
            }
        }
        return
            bands == 0
            || catalog.Read(CatalogData.Client.Systems, (long)origin)?.SecurityStatus is double current
                && (bands & DedComplexRules.SecurityBand(current)) != 0
            ? origin
            : 0;
    }
}
