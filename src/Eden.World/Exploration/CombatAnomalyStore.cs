using System.Collections.Concurrent;
using System.Collections.Immutable;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed partial class CombatAnomalyStore(
    WorldDatabase database,
    IPirateExploration? exploration = null,
    IMiningSites? mining = null,
    IWormholeTravel? wormholes = null
) : ICombatAnomalies, IDedComplexTravel, IDedEscalations
{
    private sealed record View(
        long Version,
        long Catalog,
        long Deadline,
        IReadOnlyList<CombatAnomaly> Sites,
        bool MajorThreat = false,
        bool Pirates = true
    );

    private static readonly ConcurrentDictionary<(string Path, int System), long> s_versions = new();
    private readonly ConcurrentDictionary<(int System, bool Signature), View> _views = [];
    private volatile CatalogProvider? _catalog;
    private bool _directoryMajorThreat;
    private bool _directoryPirates;
    private long _directoryVersion = -1;
    private long _directoryCatalog = -1;
    private long _directoryDeadline;
    private IReadOnlyList<AnomalySystemSites> _directory = [];
    private ImmutableArray<CombatAnomalyDefinition> _definitions;

    internal static void Invalidate(WorldDbContext world, int systemId = 0) =>
        s_versions.AddOrUpdate((world.WorldPath, systemId), 1, (_, old) => old + 1);

    private long Version(int systemId) =>
        s_versions.GetValueOrDefault((database.Path, 0)) + s_versions.GetValueOrDefault((database.Path, systemId));

    private readonly ConcurrentDictionary<
        int,
        (IReadOnlyList<CombatAnomaly> Combat, IReadOnlyList<CombatAnomaly> Mining, IReadOnlyList<CombatAnomaly> Combined)
    > _anomalies = [];

    public IReadOnlyList<CombatAnomaly> List(int systemId, long now)
    {
        return LiveWorld.Execute(database.Path, systemId, () => ListOwned(systemId, now), nameof(List));
    }

    private IReadOnlyList<CombatAnomaly> ListOwned(int systemId, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            var combat = ListCore(systemId, now, false);
            if (mining is null)
            {
                return combat;
            }
            var resources = mining.Anomalies(systemId, now);
            if (
                _anomalies.TryGetValue(systemId, out var view)
                && ReferenceEquals(view.Combat, combat)
                && ReferenceEquals(view.Mining, resources)
            )
            {
                return view.Combined;
            }
            var combined = combat.Concat(resources).ToArray();
            _anomalies[systemId] = (combat, resources, combined);
            return combined;
        }
    }

    private readonly ConcurrentDictionary<
        int,
        (
            IReadOnlyList<CombatAnomaly> Combat,
            IReadOnlyList<CombatAnomaly> Ded,
            IReadOnlyList<CombatAnomaly> Exploration,
            IReadOnlyList<CombatAnomaly> Mining,
            IReadOnlyList<CombatAnomaly> Wormholes,
            IReadOnlyList<CombatAnomaly> Combined
        )
    > _signatures = [];

    public IReadOnlyList<CombatAnomaly> Signatures(int systemId, long now)
    {
        return LiveWorld.Execute(database.Path, systemId, () => SignaturesOwned(systemId, now), nameof(Signatures));
    }

    private IReadOnlyList<CombatAnomaly> SignaturesOwned(int systemId, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            var combat = ListCore(systemId, now, true);
            var ded = DedSignatures(systemId, now);
            var other = exploration?.Signatures(systemId, now) ?? Array.Empty<CombatAnomaly>();
            var resources = mining?.Signatures(systemId, now) ?? Array.Empty<CombatAnomaly>();
            var connections = wormholes?.Signatures(systemId) ?? Array.Empty<CombatAnomaly>();
            if (
                _signatures.TryGetValue(systemId, out var view)
                && ReferenceEquals(view.Combat, combat)
                && ReferenceEquals(view.Ded, ded)
                && ReferenceEquals(view.Exploration, other)
                && ReferenceEquals(view.Mining, resources)
                && ReferenceEquals(view.Wormholes, connections)
            )
            {
                return view.Combined;
            }
            if (ded.Count == 0 && other.Count == 0 && resources.Count == 0 && connections.Count == 0)
            {
                return combat;
            }
            var combined = combat.Concat(ded).Concat(other).Concat(resources).Concat(connections).ToArray();
            _signatures[systemId] = (combat, ded, other, resources, connections, combined);
            return combined;
        }
    }

    private IReadOnlyList<CombatAnomaly> ListCore(int systemId, long now, bool signature)
    {
        return LiveWorld.Execute(database.Path, systemId, () => ListCoreOwned(systemId, now, signature), nameof(ListCore));
    }

    private IReadOnlyList<CombatAnomaly> ListCoreOwned(int systemId, long now, bool signature)
    {
        var key = (systemId, signature);
        // Take the resident owner before the shared movement gate. Bot execution
        // already owns the world when it requests these same views.
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            var provider = database.Catalog;
            long version = Version(systemId);
            bool majorThreat = database.Settings.NpcPopulation.MajorThreatAnomalies;
            bool pirates = database.Settings.NpcPopulation.Pirates;
            if (
                _views.TryGetValue(key, out var view)
                && view.Version == version
                && view.Catalog == provider.Identity.Generation
                && now < view.Deadline
                && view.MajorThreat == majorThreat
                && view.Pirates == pirates
            )
            {
                return view.Sites;
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            if (!ReferenceEquals(_catalog, provider))
            {
                _definitions = CombatAnomalyCatalog.Read(catalog);
                _catalog = provider;
            }
            var system = catalog.FindSystem(systemId);
            var faction = system is null ? null : NpcRegions.Pirates(system.RegionId);
            if (faction is null || _definitions.IsEmpty)
            {
                _views[key] = new(version, provider.Identity.Generation, long.MaxValue, [], majorThreat, pirates);
                return _views[key].Sites;
            }
            double security = catalog.Read(CatalogData.Client.Systems, (long)systemId)?.SecurityStatus ?? 0.7;
            var eligible = _definitions
                .Where(d =>
                    (d.Faction == faction || d.Faction == NpcPirateFaction.RogueDrones)
                    && (!d.RequiresMajorThreat || majorThreat)
                    && (
                        signature
                            ? d.Difficulty <= ProbeScanRules.MaximumSignatureDifficulty
                            : AnomalyEncounterRules.Allows(d.Name, security)
                    )
                )
                .OrderBy(d => d.DungeonId)
                .ToArray();
            if (eligible.Length == 0)
            {
                _views[key] = new(version, provider.Identity.Generation, long.MaxValue, [], majorThreat, pirates);
                return _views[key].Sites;
            }
            using WorldActionScope transaction = new(database, new(systems: [systemId]));
            var world = transaction.World;
            var latest = world
                .Filter<WorldCombatAnomaly>(s =>
                    s.SystemId == systemId
                    && s.IsSignature == signature
                    && s.Slot != DedComplexRules.SiteSlot
                    && s.Slot != DedComplexRules.EscalationSlot
                )
                .AsEnumerable()
                .GroupBy(s => s.Slot)
                .ToDictionary(group => group.Key, group => group.MaxBy(s => s.Id)!);
            int slots = Math.Min(signature ? ProbeScanRules.SignaturesPerSystem : AnomalyEncounterRules.SitesPerSystem, eligible.Length);
            long? nextId = null;
            bool changed = false;
            for (int slot = 0; slot < slots; slot++)
            {
                if (!pirates)
                {
                    break;
                }
                latest.TryGetValue(slot, out var site);
                if (site is not null && (site.ReplaceAt is null || site.ReplaceAt > now))
                {
                    continue;
                }
                long id = nextId ?? NextSiteId(world);
                nextId = checked(id + 1);
                uint choice = NpcSpawnRules.Seed(systemId, slot, checked((int)(id - CombatAnomalyRules.FirstSiteId)));
                var definition = eligible[(int)(choice % eligible.Length)];
                var anchor = catalog.CelestialsInSystem(systemId).OrderBy(c => c.Id).First();
                double offset = CombatAnomalyRules.PlacementOffset * (slot + (signature ? AnomalyEncounterRules.SitesPerSystem : 0) + 1);
                site = new()
                {
                    Id = id,
                    SystemId = systemId,
                    Slot = slot,
                    IsSignature = signature,
                    DungeonId = definition.DungeonId,
                    DefinitionVersion = AnomalyEncounterRules.CurrentDefinitionVersion,
                    X = anchor.X + offset,
                    Y = anchor.Y + offset,
                    Z = anchor.Z - offset,
                };
                world.Add(site);
                changed = true;
                latest[slot] = site;
            }
            foreach (var site in latest.Values.Where(s => s.CompletedAt is null))
            {
                while (site.NextWaveAt is long due && due <= now)
                {
                    site.NextWaveAt = null;
                    NextWave(world, catalog, site, due);
                    world.SaveChanges();
                    changed = true;
                }
            }
            if (changed)
            {
                Invalidate(world, -1);
            }
            transaction.Commit();
            long deadline = latest
                .Values.Where(s => s.ReplaceAt.HasValue)
                .Select(s => s.ReplaceAt!.Value)
                .DefaultIfEmpty(long.MaxValue)
                .Min();
            if (!pirates)
            {
                deadline = long.MaxValue;
            }
            deadline = Math.Min(
                deadline,
                latest.Values.Where(s => s.NextWaveAt.HasValue).Select(s => s.NextWaveAt!.Value).DefaultIfEmpty(long.MaxValue).Min()
            );
            var sites = latest
                .Values.Where(s => s.CompletedAt is null)
                .OrderBy(s => s.Id)
                .Select(s => Contact(s, _definitions.Single(d => d.DungeonId == s.DungeonId)))
                .ToArray();
            _views[key] = new(version, provider.Identity.Generation, deadline, sites, majorThreat, pirates);
            return sites;
        }
    }

    private static CombatAnomaly Contact(WorldCombatAnomaly site, CombatAnomalyDefinition definition)
    {
        var position = new SpacePosition(site.X, site.Y, site.Z);
        var objects = definition
            .Objects.Select(
                (o, index) =>
                    o with
                    {
                        Id = checked(
                            CombatAnomalyRules.FirstObjectId
                            + (site.Id - CombatAnomalyRules.FirstSiteId) * AnomalyEncounterRules.ObjectsPerSite
                            + index
                        ),
                        Position = position + o.Position,
                    }
            )
            .ToArray();
        return new(
            site.Id,
            ScannerTargetIds.FromCombatSequence(site.Id - CombatAnomalyRules.FirstSiteId),
            site.SystemId,
            site.DungeonId,
            definition.NameId,
            definition.Difficulty,
            position,
            site.Wave >= 0,
            objects,
            (int)definition.Faction,
            site.IsSignature
        );
    }

    public void ArriveUncloaked(int systemId, SpacePosition position, long now) => exploration?.ArriveUncloaked(systemId, position, now);

    public void Arrive(int systemId, SpacePosition position, long now)
    {
        LiveWorld.Execute(database.Path, systemId, () => ArriveOwned(systemId, position, now), nameof(Arrive));
    }

    private void ArriveOwned(int systemId, SpacePosition position, long now)
    {
        // Take the resident owner before the shared movement gate. Bot execution
        // already owns the world when it requests these same views.
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            if (DedSignatures(systemId, now).SingleOrDefault() is { } ded)
            {
                ArriveDed(ded, _dedViews[systemId].Profile!, position, now);
            }
            var nearby = List(systemId, now)
                .Concat(Signatures(systemId, now))
                .Where(s =>
                    s.Exploration is null
                    && s.Mining is null
                    && !s.Ded
                    && !s.Wormhole
                    && !s.Started
                    && position.DistanceTo(s.Position) <= CombatAnomalyRules.ArrivalRange
                )
                .ToArray();
            if (nearby.Length == 0)
            {
                return;
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            using WorldActionScope transaction = new(database, new(systems: [systemId]));
            var world = transaction.World;
            foreach (var contact in nearby)
            {
                var site = world.Set<WorldCombatAnomaly>().Find(contact.Id)!;
                if (site.Wave < 0 && site.CompletedAt is null)
                {
                    NextWave(world, catalog, site, now);
                }
            }
            Invalidate(world, systemId);
            transaction.Commit();
        }
    }

    internal static void Killed(WorldDbContext world, CatalogDbContext catalog, WorldNpc npc, long now, long pilot = 0)
    {
        var action = new SqlWorldAction(world);
        Killed(action, catalog, npc, now, pilot);
        world.AfterCommit(action.PublishActions);
    }

    public IReadOnlyList<AnomalySystemSites> Directory(long now)
    {
        return LiveWorld.Execute(database.Path, () => DirectoryOwned(now), nameof(Directory));
    }

    private IReadOnlyList<AnomalySystemSites> DirectoryOwned(long now)
    {
        // Take the resident owner before the shared movement gate. Bot execution
        // already owns the world when it requests these same views.
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            long version = Version(-1);
            if (
                _directoryVersion == version
                && now < _directoryDeadline
                && _directoryCatalog == database.Catalog.Identity.Generation
                && _directoryMajorThreat == database.Settings.NpcPopulation.MajorThreatAnomalies
                && _directoryPirates == database.Settings.NpcPopulation.Pirates
            )
            {
                return _directory;
            }
            int[] systems = WorldActionScope.Inspect(
                database,
                world => world.Filter<WorldCombatAnomaly>(_ => true).Select(s => s.SystemId).Distinct().ToArray()
            );
            _directory = systems.Order().Select(id => new AnomalySystemSites(id, ListCore(id, now, false))).ToArray();
            _directoryDeadline = systems.Select(id => _views[(id, false)].Deadline).DefaultIfEmpty(long.MaxValue).Min();
            _directoryVersion = Version(-1);
            _directoryCatalog = database.Catalog.Identity.Generation;
            _directoryMajorThreat = database.Settings.NpcPopulation.MajorThreatAnomalies;
            _directoryPirates = database.Settings.NpcPopulation.Pirates;
            return _directory;
        }
    }

    private static void Complete(WorldCombatAnomaly site, long now)
    {
        site.NextWaveAt = null;
        site.CompletedAt = now;
        site.ReplaceAt = checked(now + CombatAnomalyRules.ReplacementSeconds * TimeSpan.TicksPerSecond);
    }
}
