using System.Collections.Concurrent;
using System.Security.Cryptography;
using Eden.Data.Catalog.Exploration;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Docking;
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
    IPilotDamageEvents damageEvents
) : IPirateExploration, IPilotHacking
{
    private sealed record View(long Catalog, long Deadline, bool Enabled, IReadOnlyList<CombatAnomaly> Sites, long Version = 0);

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
            long generation = database.Catalog.Identity.Generation;
            bool enabled = database.Settings.NpcPopulation.Pirates;
            long version = WormholeRules.IsWormholeSystem(systemId) ? CombatAnomalyStore.ResidentVersion(database.Path, systemId) : 0;
            if (
                _views.TryGetValue(systemId, out var view)
                && view.Catalog == generation
                && view.Enabled == enabled
                && view.Version == version
                && now < view.Deadline
            )
            {
                return view.Sites;
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            var system = catalog.FindSystem(systemId);
            var faction = system is null ? null : NpcRegions.Pirates(system.RegionId);
            if (system is null || !enabled || faction is null && !WormholeRules.IsWormholeSystem(systemId))
            {
                _views[systemId] = new(generation, long.MaxValue, enabled, [], version);
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
            foreach (var kind in Enum.GetValues<ExplorationKind>())
            {
                if (wormhole && kind != ExplorationKind.Ghost && wormholeClass is not (>= 1 and <= 6))
                {
                    continue;
                }
                long? siteId = world
                    .Filter<SavedExplorationSite>(s => s.SystemId == systemId && s.Kind == (int)kind)
                    .Select(s => (long?)s.Id)
                    .OrderByDescending(id => id)
                    .FirstOrDefault();
                var row = siteId is long selected ? world.Set<SavedExplorationSite>().Find(selected) : null;
                if (row is not null && row.ExpiresAt <= now && row.ReplaceAt is null)
                {
                    Retire(world, row, row.ExpiresAt);
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
                                    group == (kind == ExplorationKind.Ghost ? GhostSiteRules.ContainerGroup : HackingRules.ContainerGroup)
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
                    double offset = CombatAnomalyRules.PlacementOffset * (10 + (int)kind);
                    row = new()
                    {
                        Id = id,
                        SystemId = systemId,
                        Kind = (int)kind,
                        DungeonId = definition.DungeonId,
                        X = anchor.X - offset,
                        Y = anchor.Y + offset,
                        Z = anchor.Z + offset,
                        ExpiresAt = checked(now + (definition.WormholeClass > 0 ? SleeperRules.SiteLifetime : HackingRules.SiteLifetime)),
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
                                    HackingRules.FirstContainerId + (id - HackingRules.FirstSiteId) * HackingRules.ContainersPerSite + index
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
                    guarded = CombatAnomalyStore.EnsureGuarded(world, catalog, row.Id, systemId, row.DungeonId, new(row.X, row.Y, row.Z));
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
                        GhostCountdownEnd: row.ResponseSpawned && !row.Detonated ? row.ResponseAt + GhostSiteRules.VisibleCountdown : null,
                        GhostRoomId: profile.RoomId
                    )
                );
                deadline = Math.Min(deadline, row.ExpiresAt);
            }
            transaction.Commit();
            _views[systemId] = new(generation, deadline, enabled, sites.ToArray(), version);
            return _views[systemId].Sites;
        }
    }

    private static ExplorationDefinition Definition(CatalogDbContext catalog, SavedExplorationSite site) =>
        site.Kind == (int)ExplorationKind.Ghost ? GhostSiteCatalog.Find(catalog, site.DungeonId)
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
