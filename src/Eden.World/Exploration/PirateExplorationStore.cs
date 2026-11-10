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
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed partial class PirateExplorationStore(WorldDatabase database) : IPirateExploration, IPilotHacking
{
    private sealed record View(long Catalog, long Deadline, bool Enabled, IReadOnlyList<CombatAnomaly> Sites);

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
            if (_views.TryGetValue(systemId, out var view) && view.Catalog == generation && view.Enabled == enabled && now < view.Deadline)
            {
                return view.Sites;
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            var system = catalog.FindSystem(systemId);
            var faction = system is null ? null : NpcRegions.Pirates(system.RegionId);
            if (faction is null || !enabled)
            {
                _views[systemId] = new(generation, long.MaxValue, enabled, []);
                return _views[systemId].Sites;
            }
            using WorldDbContext world = new(database.Path);
            using var transaction = world.Database.BeginTransaction();
            double security = catalog.Read(CatalogData.Client.Systems, (long)systemId)?.SecurityStatus ?? 0.7;
            int band =
                security >= 0.45 ? 0
                : security > 0 ? 1
                : 2;
            List<CombatAnomaly> sites = [];
            long deadline = long.MaxValue;
            foreach (var kind in Enum.GetValues<ExplorationKind>())
            {
                var row = world
                    .Set<SavedExplorationSite>()
                    .Where(s => s.SystemId == systemId && s.Kind == (int)kind)
                    .OrderByDescending(s => s.Id)
                    .FirstOrDefault();
                if (row is not null && row.ExpiresAt <= now && row.ReplaceAt is null)
                {
                    Retire(world, row, row.ExpiresAt);
                }
                if (row is null || row.ReplaceAt <= now)
                {
                    var eligible = HackingCatalog
                        .Sites.Where(s =>
                            s.FactionId == (int)faction
                            && s.Kind == kind
                            && s.Band == band
                            && s.Containers.All(c => catalog.FittingType(c.TypeId)?.GroupId == HackingRules.ContainerGroup)
                        )
                        .ToArray();
                    if (eligible.Length == 0)
                    {
                        continue;
                    }
                    long id = Math.Max(HackingRules.FirstSiteId, (world.Set<SavedExplorationSite>().Max(s => (long?)s.Id) ?? 0) + 1);
                    var definition = eligible[(int)((id + systemId) % eligible.Length)];
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
                        ExpiresAt = checked(now + HackingRules.SiteLifetime),
                    };
                    world.Add(row);
                    for (int index = 0; index < definition.Containers.Count; index++)
                    {
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
                var profile = Definition(row);
                var containers = world.Set<SavedHackingContainer>().Where(c => c.SiteId == row.Id).OrderBy(c => c.Index).ToArray();
                if (containers.All(c => c.Destroyed || c.Hacked && c.Empty))
                {
                    Retire(world, row, now);
                    deadline = Math.Min(deadline, row.ReplaceAt!.Value);
                    continue;
                }
                var busy = world
                    .Set<SavedHackingAttempt>()
                    .Where(a => containers.Select(c => c.Id).Contains(a.ContainerId))
                    .Select(a => a.ContainerId)
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
                        false,
                        containers.Where(c => !c.Destroyed).Select(c => Object(catalog, row, c, busy.Contains(c.Id))).ToArray(),
                        profile.FactionId,
                        true,
                        kind
                    )
                );
                deadline = Math.Min(deadline, row.ExpiresAt);
            }
            world.SaveChanges();
            transaction.Commit();
            _views[systemId] = new(generation, deadline, enabled, sites.ToArray());
            return _views[systemId].Sites;
        }
    }

    private static ExplorationDefinition Definition(SavedExplorationSite site) =>
        HackingCatalog.Sites.Single(d => d.DungeonId == site.DungeonId);

    private static StaticSpaceObject Object(
        CatalogDbContext catalog,
        SavedExplorationSite site,
        SavedHackingContainer container,
        bool busy = false
    )
    {
        var definition = Definition(site).Containers[container.Index];
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

    private void Retire(WorldDbContext world, SavedExplorationSite site, long now)
    {
        site.ReplaceAt = checked(now + HackingRules.ReplacementDelay);
        var ids = world.Set<SavedHackingContainer>().Where(c => c.SiteId == site.Id).Select(c => c.Id).ToArray();
        foreach (var attempt in world.Set<SavedHackingAttempt>().Where(a => ids.Contains(a.ContainerId)).ToArray())
        {
            ReleaseModule(world, attempt);
            world.Remove(attempt);
        }
        world.Set<OwnedItem>().Where(i => ids.Contains(i.LocationId)).ExecuteDelete();
        _views.TryRemove(site.SystemId, out _);
    }

    private void CompleteIfEmpty(WorldDbContext world, SavedExplorationSite site, long now)
    {
        world.SaveChanges();
        if (!world.Set<SavedHackingContainer>().Any(c => c.SiteId == site.Id && !c.Destroyed && (!c.Hacked || !c.Empty)))
        {
            Retire(world, site, now);
        }
        _views.TryRemove(site.SystemId, out _);
    }

    private static void GrantLoot(
        WorldDbContext world,
        CatalogDbContext catalog,
        SavedHackingContainer container,
        ExplorationDefinition definition,
        long pilotId
    )
    {
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
    }
}
