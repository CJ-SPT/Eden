using System.Collections.Concurrent;
using System.Collections.Immutable;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed class CombatAnomalyStore(WorldDatabase database, IPirateExploration? exploration = null, IMiningSites? mining = null)
    : ICombatAnomalies
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
    private readonly Dictionary<(int System, bool Signature), View> _views = [];
    private CatalogProvider? _catalog;
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

    private readonly Dictionary<
        int,
        (IReadOnlyList<CombatAnomaly> Combat, IReadOnlyList<CombatAnomaly> Mining, IReadOnlyList<CombatAnomaly> Combined)
    > _anomalies = [];

    public IReadOnlyList<CombatAnomaly> List(int systemId, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        lock (database.MotionLock)
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

    private readonly Dictionary<
        int,
        (
            IReadOnlyList<CombatAnomaly> Combat,
            IReadOnlyList<CombatAnomaly> Exploration,
            IReadOnlyList<CombatAnomaly> Mining,
            IReadOnlyList<CombatAnomaly> Combined
        )
    > _signatures = [];

    public IReadOnlyList<CombatAnomaly> Signatures(int systemId, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        lock (database.MotionLock)
        {
            var combat = ListCore(systemId, now, true);
            if (exploration is null && mining is null)
            {
                return combat;
            }
            var other = exploration?.Signatures(systemId, now) ?? Array.Empty<CombatAnomaly>();
            var resources = mining?.Signatures(systemId, now) ?? Array.Empty<CombatAnomaly>();
            if (
                _signatures.TryGetValue(systemId, out var view)
                && ReferenceEquals(view.Combat, combat)
                && ReferenceEquals(view.Exploration, other)
                && ReferenceEquals(view.Mining, resources)
            )
            {
                return view.Combined;
            }
            var combined = combat.Concat(other).Concat(resources).ToArray();
            _signatures[systemId] = (combat, other, resources, combined);
            return combined;
        }
    }

    private IReadOnlyList<CombatAnomaly> ListCore(int systemId, long now, bool signature)
    {
        var key = (systemId, signature);
        // Take the resident owner before the shared movement gate. Bot execution
        // already owns the world when it requests these same views.
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        lock (database.MotionLock)
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
            using WorldDbContext world = new(database.Path);
            using var transaction = world.Database.BeginTransaction();
            var latest = world
                .Set<WorldCombatAnomaly>()
                .Where(s =>
                    s.SystemId == systemId
                    && s.IsSignature == signature
                    && !world
                        .Set<WorldCombatAnomaly>()
                        .Any(n => n.SystemId == systemId && n.Slot == s.Slot && n.IsSignature == signature && n.Id > s.Id)
                )
                .ToDictionary(s => s.Slot);
            int slots = Math.Min(signature ? ProbeScanRules.SignaturesPerSystem : AnomalyEncounterRules.SitesPerSystem, eligible.Length);
            long nextId = Math.Max(
                CombatAnomalyRules.FirstSiteId,
                (world.Set<WorldCombatAnomaly>().Max(s => (long?)s.Id) ?? CombatAnomalyRules.FirstSiteId - 1) + 1
            );
            bool created = false;
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
                uint choice = NpcSpawnRules.Seed(systemId, slot, checked((int)(nextId - CombatAnomalyRules.FirstSiteId)));
                var definition = eligible[(int)(choice % eligible.Length)];
                var anchor = catalog.CelestialsInSystem(systemId).OrderBy(c => c.Id).First();
                double offset = CombatAnomalyRules.PlacementOffset * (slot + (signature ? AnomalyEncounterRules.SitesPerSystem : 0) + 1);
                site = new()
                {
                    Id = nextId++,
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
                created = true;
                latest[slot] = site;
            }
            foreach (var site in latest.Values.Where(s => s.CompletedAt is null))
            {
                while (site.NextWaveAt is long due && due <= now)
                {
                    site.NextWaveAt = null;
                    NextWave(world, catalog, site, due);
                    world.SaveChanges();
                }
            }
            bool changed = world.ChangeTracker.HasChanges();
            world.SaveChanges();
            transaction.Commit();
            if (changed || created)
            {
                Invalidate(world, -1);
            }
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

    public void Arrive(int systemId, SpacePosition position, long now)
    {
        // Take the resident owner before the shared movement gate. Bot execution
        // already owns the world when it requests these same views.
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        lock (database.MotionLock)
        {
            var nearby = List(systemId, now)
                .Concat(Signatures(systemId, now))
                .Where(s =>
                    s.Exploration is null
                    && s.Mining is null
                    && !s.Started
                    && position.DistanceTo(s.Position) <= CombatAnomalyRules.ArrivalRange
                )
                .ToArray();
            if (nearby.Length == 0)
            {
                return;
            }
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            foreach (var contact in nearby)
            {
                var site = world.Set<WorldCombatAnomaly>().Find(contact.Id)!;
                if (site.Wave < 0 && site.CompletedAt is null)
                {
                    NextWave(world, catalog, site, now);
                }
            }
            world.SaveChanges();
            transaction.Commit();
            Invalidate(world, systemId);
        }
    }

    internal static void Killed(WorldDbContext world, CatalogDbContext catalog, WorldNpc npc, long now)
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
            if (!site.NextWaveAt.HasValue && !alive.Any(n => blocking.Contains(n.Id)))
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

    public IReadOnlyList<AnomalySystemSites> Directory(long now)
    {
        // Take the resident owner before the shared movement gate. Bot execution
        // already owns the world when it requests these same views.
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        lock (database.MotionLock)
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
            int[] systems;
            using (WorldDbContext world = new(database.Path))
            {
                systems = world.Set<WorldCombatAnomaly>().Select(s => s.SystemId).Distinct().ToArray();
            }
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

    private static void NextWave(WorldDbContext world, CatalogDbContext catalog, WorldCombatAnomaly site, long now)
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
        WorldDbContext world,
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
