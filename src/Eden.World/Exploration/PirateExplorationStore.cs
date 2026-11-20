using System.Collections.Concurrent;
using System.Security.Cryptography;
using Eden.Data.Catalog.Exploration;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.FactionWarfare;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed partial class PirateExplorationStore(
    WorldDatabase database,
    SqliteWorld lookup,
    PilotLossSettlement losses,
    IPilotDamageEvents damageEvents,
    Eden.Gameplay.Sovereignty.ISovereigntyResources? sovereignty = null
) : IPirateExploration, IPilotHacking
{
    private sealed record View(
        long Catalog,
        long Deadline,
        bool Enabled,
        IReadOnlyList<CombatAnomaly> Sites,
        long Version = 0,
        long SovereigntyVersion = 0,
        MilitiaFaction? OperationOccupier = null
    );

    private readonly ConcurrentDictionary<int, View> _views = [];

    public IReadOnlyList<CombatAnomaly> Signatures(int systemId, long now)
    {
        return LiveWorld.Execute(database.Path, systemId, () => SignaturesOwned(systemId, now), nameof(Signatures));
    }

    private IReadOnlyList<CombatAnomaly> SignaturesOwned(int systemId, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            sovereignty?.AdvanceAll();
            long sovereigntyVersion = Eden.World.Sovereignty.SovereigntySites.Version(database.Path, systemId);
            var live = LiveWorld.Find(database.Path);
            MilitiaFaction? operationOccupier =
                live is not null && FactionWarzoneCatalog.Systems.ContainsKey(systemId)
                    ? live.Economy.Inspect(world =>
                        FactionWarzoneStore.System(world, systemId)
                            is { Adjacency: WarzoneAdjacency.Rearguard, PendingTransferAt: null } info
                            ? info.Occupier
                            : (MilitiaFaction?)null
                    )
                    : null;
            long generation = database.Catalog.Identity.Generation;
            bool enabled = database.Settings.NpcPopulation.Pirates;
            long version = WormholeRules.IsWormholeSystem(systemId) ? CombatAnomalyStore.ResidentVersion(database.Path, systemId) : 0;
            if (
                _views.TryGetValue(systemId, out var view)
                && view.Catalog == generation
                && view.Enabled == enabled
                && view.Version == version
                && view.SovereigntyVersion == sovereigntyVersion
                && view.OperationOccupier == operationOccupier
                && now < view.Deadline
            )
            {
                return view.Sites;
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            var system = catalog.FindSystem(systemId);
            var faction = system is null ? null : NpcRegions.Pirates(system.RegionId);
            bool ordinary = enabled && (faction is not null || WormholeRules.IsWormholeSystem(systemId));
            bool warzone = live is not null && FactionWarzoneCatalog.Systems.ContainsKey(systemId);
            if (system is null || !ordinary && !warzone)
            {
                _views[systemId] = new(generation, long.MaxValue, enabled, [], version, sovereigntyVersion);
                return _views[systemId].Sites;
            }
            bool wormhole = WormholeRules.IsWormholeSystem(systemId);
            int wormholeClass = wormhole ? (int)(catalog.Read(CatalogData.Client.Systems, (long)systemId)?.WormholeClassID ?? 0) : 0;
            var sleepers = wormhole ? SleeperSiteCatalog.Exploration(catalog) : [];
            if (sleepers.Any(s => s.WormholeClass == wormholeClass) && LiveWorld.Find(database.Path) is null)
            {
                throw new InvalidOperationException("Guarded wormhole exploration requires the resident world.");
            }
            using WorldActionScope transaction = new(database, select: world => ExplorationParticipants(world, systemId, now));
            var world = transaction.World;
            double security = catalog.Read(CatalogData.Client.Systems, (long)systemId)?.SecurityStatus ?? 0.7;
            int band =
                security >= 0.45 ? 0
                : security > 0 ? 1
                : 2;
            List<CombatAnomaly> sites = [];
            long deadline = long.MaxValue;
            int explorationTier = Eden.World.Sovereignty.SovereigntySites.Read(world, catalog, systemId).Exploration;
            foreach (var kind in ordinary ? Enum.GetValues<ExplorationKind>() : [])
            {
                if (wormhole && kind != ExplorationKind.Ghost && wormholeClass is not (>= 1 and <= 6))
                {
                    continue;
                }
                int existingSlots = 0;
                if (world is EconomyTransaction)
                {
                    // Positive slot buckets are small even when the base site's archive is large.
                    for (
                        int candidate = 1;
                        candidate <= Eden.Gameplay.Sovereignty.SovereigntyResourceRules.MaximumDetectionTier;
                        candidate++
                    )
                    {
                        if (
                            world
                                .Filter<SavedExplorationSite>(site =>
                                    site.SovereigntySlot == candidate && site.SystemId == systemId && site.Kind == (int)kind
                                )
                                .Any()
                        )
                        {
                            existingSlots = candidate;
                        }
                    }
                }
                int slots = wormhole || kind == ExplorationKind.Ghost ? 0 : Math.Max(explorationTier, existingSlots);
                for (int sovereigntySlot = 0; sovereigntySlot <= slots; sovereigntySlot++)
                {
                    var candidatesForSlot = world.Filter<SavedExplorationSite>(site =>
                        site.SystemId == systemId && site.Kind == (int)kind && site.SovereigntySlot >= 0
                    );
                    if (slots > 0)
                    {
                        candidatesForSlot = candidatesForSlot.Where(site => site.SovereigntySlot == sovereigntySlot);
                    }
                    long? siteId = candidatesForSlot.Select(site => (long?)site.Id).OrderByDescending(id => id).FirstOrDefault();
                    var row = siteId is long selected ? world.Set<SavedExplorationSite>().Find(selected) : null;
                    if (row is not null && row.ExpiresAt <= now && row.ReplaceAt is null)
                    {
                        Retire(world, row, row.ExpiresAt);
                    }
                    if (sovereigntySlot > explorationTier && (row is null || row.ReplaceAt is not null))
                    {
                        continue;
                    }
                    if (row is null || row.ReplaceAt <= now)
                    {
                        IEnumerable<ExplorationDefinition> candidates =
                            kind == ExplorationKind.Ghost ? GhostSiteCatalog.Read(catalog)
                            : wormhole ? sleepers
                            : HackingCatalog.Sites;
                        var eligible = candidates
                            .Where(s =>
                                (wormhole || s.FactionId == (int?)faction)
                                && s.Kind == kind
                                && (
                                    wormhole && kind != ExplorationKind.Ghost
                                        ? s.WormholeClass == wormholeClass
                                        : s.Band == (wormhole ? GhostSiteRules.WormholeBand : band)
                                )
                                && s.Containers.All(c =>
                                    catalog.FittingType(c.TypeId)?.GroupId is int group
                                    && (
                                        group
                                            == (kind == ExplorationKind.Ghost ? GhostSiteRules.ContainerGroup : HackingRules.ContainerGroup)
                                        || s.WormholeClass > 0 && group == SleeperRules.RelicContainerGroup
                                    )
                                )
                            )
                            .ToArray();
                        if (eligible.Length == 0)
                        {
                            continue;
                        }
                        long id = Math.Max(
                            HackingRules.FirstSiteId,
                            (world.Filter<SavedExplorationSite>(_ => true).Max(s => (long?)s.Id) ?? 0) + 1
                        );
                        int prior =
                            row is not null && wormhole && kind != ExplorationKind.Ghost
                                ? Array.FindIndex(eligible, candidate => candidate.DungeonId == row.DungeonId)
                                : -1;
                        var definition = eligible[(int)((prior >= 0 ? prior + 1 : id + systemId) % eligible.Length)];
                        var anchor = catalog.CelestialsInSystem(systemId).OrderBy(c => c.Id).FirstOrDefault();
                        if (anchor is null)
                        {
                            continue;
                        }
                        double offset = CombatAnomalyRules.PlacementOffset * (10 + (int)kind + sovereigntySlot * 4);
                        row = new()
                        {
                            Id = id,
                            SystemId = systemId,
                            Kind = (int)kind,
                            SovereigntySlot = sovereigntySlot,
                            DungeonId = definition.DungeonId,
                            X = anchor.X - offset,
                            Y = anchor.Y + offset,
                            Z = anchor.Z + offset,
                            ExpiresAt = checked(
                                now + (definition.WormholeClass > 0 ? SleeperRules.SiteLifetime : HackingRules.SiteLifetime)
                            ),
                        };
                        world.Add(row);
                        for (int index = 0; index < definition.Containers.Count; index++)
                        {
                            if (definition.Containers[index].Optional && !SleeperRules.HasTalocan(id))
                            {
                                continue;
                            }
                            world.Add(
                                new SavedHackingContainer
                                {
                                    Id = checked(
                                        HackingRules.FirstContainerId
                                        + (id - HackingRules.FirstSiteId) * HackingRules.ContainersPerSite
                                        + index
                                    ),
                                    SiteId = id,
                                    Index = index,
                                    LootSeed = RandomNumberGenerator.GetInt32(int.MaxValue),
                                }
                            );
                        }
                        world.SaveChanges();
                    }
                    if (row.ReplaceAt is long due)
                    {
                        deadline = Math.Min(deadline, due);
                        continue;
                    }
                    var profile = Definition(catalog, row);
                    WorldCombatAnomaly? guarded = null;
                    if (profile.WormholeClass > 0)
                    {
                        guarded = CombatAnomalyStore.EnsureGuarded(
                            world,
                            catalog,
                            row.Id,
                            systemId,
                            row.DungeonId,
                            new(row.X, row.Y, row.Z)
                        );
                        deadline = Math.Min(deadline, CombatAnomalyStore.AdvanceGuarded(world, catalog, row.Id, now));
                    }
                    if (kind == ExplorationKind.Ghost)
                    {
                        deadline = Math.Min(deadline, AdvanceGhostSite(world, catalog, row, profile, now));
                    }
                    var containers = world.Filter<SavedHackingContainer>(c => c.SiteId == row.Id).OrderBy(c => c.Index).ToArray();
                    if (profile.WormholeClass > 0)
                    {
                        RecoverSleeperHacks(world, catalog, row, containers, now);
                    }
                    if (containers.All(c => c.Destroyed || c.Hacked && c.Empty))
                    {
                        Retire(world, row, now);
                        deadline = Math.Min(deadline, row.ReplaceAt!.Value);
                        continue;
                    }
                    var busy = containers
                        .Where(c => world.Filter<SavedHackingAttempt>(a => a.ContainerId == c.Id).Any())
                        .Select(c => c.Id)
                        .ToHashSet();
                    sites.Add(
                        new(
                            row.Id,
                            ScannerTargetIds.FromExplorationSequence(row.Id - HackingRules.FirstSiteId),
                            systemId,
                            profile.DungeonId,
                            profile.NameId,
                            profile.Difficulty,
                            new(row.X, row.Y, row.Z),
                            row.ResponseAt is not null || guarded is { Wave: >= 0 },
                            containers
                                .Where(c => !c.Destroyed)
                                .Select(c => Object(catalog, row, c, busy.Contains(c.Id), profile))
                                .Concat(
                                    profile.Scenery.Select(
                                        (body, index) =>
                                            body with
                                            {
                                                Id =
                                                    GhostSiteRules.FirstSceneryId
                                                    + (row.Id - HackingRules.FirstSiteId) * HackingRules.ContainersPerSite
                                                    + index,
                                                Position = new SpacePosition(row.X, row.Y, row.Z) + body.Position,
                                            }
                                    )
                                )
                                .ToArray(),
                            profile.FactionId,
                            true,
                            kind,
                            GhostCountdownEnd: row.ResponseSpawned && !row.Detonated
                                ? row.ResponseAt + GhostSiteRules.VisibleCountdown
                                : null,
                            GhostRoomId: profile.RoomId
                        )
                    );
                    deadline = Math.Min(deadline, row.ExpiresAt);
                }
            }
            if (warzone)
            {
                ReadOperationCenter(world, catalog, systemId, operationOccupier, now, sites, ref deadline);
            }
            transaction.Commit();
            _views[systemId] = new(generation, deadline, enabled, sites.ToArray(), version, sovereigntyVersion, operationOccupier);
            return _views[systemId].Sites;
        }
    }

    private static ExplorationDefinition Definition(CatalogDbContext catalog, SavedExplorationSite site) =>
        site.SovereigntySlot == MilitiaOperationSlot
            ? MilitiaOperationCatalog.Find(catalog, site.DungeonId) ?? throw new InvalidDataException("Unknown Operation Center.")
        : site.Kind == (int)ExplorationKind.Ghost ? GhostSiteCatalog.Find(catalog, site.DungeonId)
        : WormholeRules.IsWormholeSystem(site.SystemId)
        && SleeperSiteCatalog.Exploration(catalog).FirstOrDefault(d => d.DungeonId == site.DungeonId) is { } sleeper
            ? sleeper
        : HackingCatalog.Sites.Single(d => d.DungeonId == site.DungeonId);

    private static StaticSpaceObject Object(
        CatalogDbContext catalog,
        SavedExplorationSite site,
        SavedHackingContainer container,
        bool busy = false,
        ExplorationDefinition? profile = null
    )
    {
        var definition = (profile ?? Definition(catalog, site)).Containers[container.Index];
        var type =
            catalog.FittingType(definition.TypeId) ?? throw new InvalidDataException("The exploration container type is unavailable.");
        return new(
            container.Id,
            type.Id,
            type.GroupId,
            type.CategoryId,
            checked((int)container.OwnerId),
            new SpacePosition(site.X, site.Y, site.Z) + definition.Position,
            (float)(type.Radius ?? 0)
        )
        {
            Name = type.Name,
            Solid = false,
            IsGlobal = false,
            HackingState =
                container.Hacked ? 2
                : busy ? 1
                : 0,
            Container = new(container.OwnerId, container.Empty),
        };
    }

    private void Retire(WorldAction world, SavedExplorationSite site, long now)
    {
        site.ReplaceAt = checked(now + HackingRules.ReplacementDelay);
        RetireResponse(world, site, now);
        CombatAnomalyStore.RetireGuarded(world, site.Id, now);
        var containers = world.Filter<SavedHackingContainer>(c => c.SiteId == site.Id).ToArray();
        foreach (var container in containers)
        {
            if (
                world
                    .Filter<SavedHackingAttempt>(a => a.ContainerId == container.Id)
                    .AsEnumerable()
                    .SingleOrDefault(a => world.Entry(a).State != EntityState.Deleted) is
                { } attempt
            )
            {
                ReleaseModule(world, attempt);
                world.Remove(attempt);
            }
            world.RemoveRange(world.Filter<OwnedItem>(i => i.LocationId == container.Id).ToArray());
        }
        Invalidate(world, site.SystemId);
    }

    private void CompleteIfEmpty(WorldAction world, SavedExplorationSite site, long now)
    {
        world.SaveChanges();
        if (!world.Filter<SavedHackingContainer>(c => c.SiteId == site.Id && !c.Destroyed && (!c.Hacked || !c.Empty)).Any())
        {
            Retire(world, site, now);
        }
        Invalidate(world, site.SystemId);
    }

    private void Invalidate(WorldAction world, int system) => world.AfterCommit(() => _views.TryRemove(system, out _));

    private static void GrantLoot(
        WorldAction world,
        CatalogDbContext catalog,
        SavedHackingContainer container,
        ExplorationDefinition definition,
        long pilotId
    )
    {
        if (MilitiaAdvantageRules.IsEmpire(definition.FactionId))
        {
            int component = MilitiaOperationCatalog.Loot((MilitiaFaction)definition.FactionId, container.Index);
            InventoryPersistence.MergeOutput(world, catalog, pilotId, container.Id, (int)InventoryFlag.Cargo, component, 1);
            return;
        }
        if (definition.WormholeClass > 0)
        {
            GrantSleeperLoot(world, catalog, container, definition, pilotId);
            return;
        }
        if (definition.Kind == ExplorationKind.Ghost)
        {
            int cache = definition.Containers[container.Index].TypeId;
            foreach (var stack in GhostSiteRules.Loot(container.LootSeed, definition.Band, GhostSiteRules.IsBonusCache(cache)))
            {
                if (catalog.Read(CatalogData.Client.Types, (long)stack.TypeId)?.Published != 1)
                {
                    throw new InvalidDataException("The Covert Research Facility reward is unavailable.");
                }
                InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    pilotId,
                    container.Id,
                    (int)InventoryFlag.Cargo,
                    stack.TypeId,
                    stack.Quantity
                );
            }
            if (GhostSiteRules.Blueprint(container.LootSeed, definition.Band, cache) is int blueprint)
            {
                var type = catalog.Require(CatalogData.Client.Types, (long)blueprint);
                var recipe = catalog.Read(
                    CatalogData.Client.Blueprints,
                    blueprint.ToString(System.Globalization.CultureInfo.InvariantCulture)
                );
                if (type.Published != 1 || recipe?.Activities?.Manufacturing is null)
                {
                    throw new InvalidDataException("The Covert Research Facility blueprint is unavailable.");
                }
                world.Add(
                    new OwnedItem
                    {
                        Id = InventoryPersistence.NextId(world),
                        OwnerId = pilotId,
                        LocationId = container.Id,
                        FlagId = (int)InventoryFlag.Cargo,
                        TypeId = blueprint,
                        Singleton = true,
                        Quantity = 1,
                        BlueprintRuns = 1,
                    }
                );
            }
            return;
        }
        if (
            world is Eden.World.Runtime.Economy.EconomyTransaction
            && definition.Kind == ExplorationKind.Data
            && AbyssalContent.DataSiteFilament(container.LootSeed) is int filament
            && catalog.FittingType(filament) is not null
        )
        {
            InventoryPersistence.MergeOutput(world, catalog, pilotId, container.Id, (int)InventoryFlag.Cargo, filament, 1);
        }
        var datacores = definition.Kind == ExplorationKind.Data ? HackingCatalog.Loot(catalog, HackingCatalog.DatacoreGroup) : [];
        var decryptors = definition.Kind == ExplorationKind.Data ? HackingCatalog.Loot(catalog, HackingCatalog.DecryptorGroup) : [];
        var loot = ExplorationLootRules.Roll(
            container.LootSeed,
            definition.Kind,
            (NpcPirateFaction)definition.FactionId,
            definition.Band,
            datacores,
            decryptors
        );
        foreach (var stack in loot)
        {
            if (
                catalog.FittingType(stack.TypeId) is not { } type
                || catalog.Read(CatalogData.Client.Types, (long)stack.TypeId)?.Published != 1
                || (
                    definition.Kind == ExplorationKind.Relic
                        ? type.GroupId != HackingCatalog.SalvageGroup
                        : type.GroupId is not (HackingCatalog.DatacoreGroup or HackingCatalog.DecryptorGroup)
                )
            )
            {
                throw new InvalidDataException("The exploration reward type is unavailable.");
            }
            InventoryPersistence.MergeOutput(world, catalog, pilotId, container.Id, (int)InventoryFlag.Cargo, stack.TypeId, stack.Quantity);
        }
        if (
            definition.Kind == ExplorationKind.Data
            && ExplorationLootRules.EncryptionSkillbook(container.LootSeed, (NpcPirateFaction)definition.FactionId) is int skillbook
            && catalog.FittingType(skillbook)?.CategoryId == (int)InventoryCategory.Skill
            && catalog.Read(CatalogData.Client.Types, (long)skillbook)?.Published == 1
        )
        {
            InventoryPersistence.MergeOutput(world, catalog, pilotId, container.Id, (int)InventoryFlag.Cargo, skillbook, 1);
        }
        if (
            definition.Kind == ExplorationKind.Data
            && ExplorationLootRules.MolecularCondenser(
                container.LootSeed,
                (NpcPirateFaction)definition.FactionId,
                definition.Containers[container.Index].TypeId
            )
                is { } condenser
            && catalog.FittingType(condenser.TypeId)?.CategoryId == (int)InventoryCategory.Commodity
            && catalog.Read(CatalogData.Client.Types, (long)condenser.TypeId)?.Published == 1
        )
        {
            InventoryPersistence.MergeOutput(
                world,
                catalog,
                pilotId,
                container.Id,
                (int)InventoryFlag.Cargo,
                condenser.TypeId,
                condenser.Quantity
            );
        }
    }
}
