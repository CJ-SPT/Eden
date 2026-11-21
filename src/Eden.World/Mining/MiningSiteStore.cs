using Eden.Data.Catalog.Mining;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Mining;

internal sealed record MiningSiteView(long Deadline, IReadOnlyList<CombatAnomaly> Sites);

[Injectable(InjectionType.Singleton)]
internal sealed partial class MiningSiteStore(WorldDatabase database, Eden.Gameplay.Sovereignty.ISovereigntyResources? sovereignty = null)
    : IMiningSites
{
    // Keep resource sites apart from combat placements and vary replacement ice fields.
    private const int GasPlacement = 25;
    private const int IcePlacement = 26;
    private const int IcePlacementSteps = 97;
    private const double IcePlacementScale = 100;

    public IReadOnlyList<CombatAnomaly> Signatures(int systemId, long now) => Get(systemId, now, MiningFamily.Gas);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        int,
        (IReadOnlyList<CombatAnomaly> Ice, IReadOnlyList<CombatAnomaly> Ore, IReadOnlyList<CombatAnomaly> Combined)
    > _combined = new();

    public IReadOnlyList<CombatAnomaly> Anomalies(int systemId, long now)
    {
        sovereignty?.AdvanceSites(systemId);
        var ice = Get(systemId, now, MiningFamily.Ice);
        var ore = SovereigntyOres(systemId, now);
        if (ore.Count == 0)
        {
            return ice;
        }
        if (_combined.TryGetValue(systemId, out var cached) && ReferenceEquals(cached.Ice, ice) && ReferenceEquals(cached.Ore, ore))
        {
            return cached.Combined;
        }
        var result = ice.Concat(ore).ToArray();
        _combined[systemId] = (ice, ore, result);
        return result;
    }

    private IReadOnlyList<CombatAnomaly> Get(int systemId, long now, MiningFamily family)
    {
        return LiveWorld.Execute(database.Path, systemId, () => GetOwned(systemId, now, family), nameof(Get));
    }

    private IReadOnlyList<CombatAnomaly> GetOwned(int systemId, long now, MiningFamily family)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            return (
                LiveReadModels
                    .Find(database.Path)
                    ?.MiningSites(systemId, family, database.Catalog.Identity.Generation, now, () => Read(systemId, now, family))
                ?? Read(systemId, now, family)
            ).Sites;
        }
    }

    private MiningSiteView Read(int systemId, long now, MiningFamily family) => ReadSites(systemId, now, family, database);

    internal static void EnsureIce(EconomyTransaction world, CatalogDbContext catalog, int systemId, long now) =>
        ReadSites(systemId, now, MiningFamily.Ice, null, world, catalog);

    private static MiningSiteView ReadSites(
        int systemId,
        long now,
        MiningFamily family,
        WorldDatabase? database,
        EconomyTransaction? resident = null,
        CatalogDbContext? existingCatalog = null
    )
    {
        using var opened = database is null ? null : new CatalogDbContext(database.CatalogPath);
        var catalog = existingCatalog ?? opened!;
        var system = catalog.FindSystem(systemId);
        if (system is null)
        {
            return new(long.MaxValue, []);
        }
        double security = catalog.Definitions.SystemSecurity.GetValueOrDefault(systemId, double.NaN);
        bool lowSecurity = security is > 0 and <= 1 && NpcSpawnRules.SecurityBand(security) == NpcSecurityBand.Low;
        int wormholeClass = WormholeRules.IsWormholeSystem(systemId)
            ? (int)(catalog.Read(CatalogData.Client.Systems, (long)systemId)?.WormholeClassID ?? 0)
            : 0;
        var eligible = MiningSiteCatalog
            .Sites.Where(s =>
                s.Family == family
                && (!s.LowSecurityOnly || lowSecurity)
                && (
                    family == MiningFamily.Ice ? s.Systems!.Contains(systemId)
                    : s.WormholeClasses is { } classes ? classes.Contains(wormholeClass)
                    : s.Regions.Contains(system.RegionId)
                )
                && s.Deposits.All(d =>
                    catalog.FittingType(d.TypeId)?.GroupId
                    == (family == MiningFamily.Ice ? MiningDefinitions.IceGroup : MiningDefinitions.GasCloudGroup)
                )
            )
            .ToArray();
        var anchor = catalog.CelestialsInSystem(systemId).OrderBy(c => c.Id).FirstOrDefault();
        if (eligible.Length == 0 || anchor is null)
        {
            return new(long.MaxValue, []);
        }
        if (
            wormholeClass > 0
            && database is not null
            && LiveWorld.Find(database.Path) is null
            && eligible.Any(e => SleeperSiteCatalog.Read(catalog).Any(s => s.DungeonId == e.DungeonId))
        )
        {
            throw new InvalidOperationException("Guarded fullerite reservoirs require the resident world.");
        }
        using var transaction = database is null ? null : new WorldActionScope(database, new(systems: [systemId]));
        var world = resident ?? transaction!.World;
        long? siteId = world
            .Filter<SavedMiningSite>(s => s.SystemId == systemId && s.Family == family)
            .Select(s => (long?)s.Id)
            .OrderByDescending(id => id)
            .FirstOrDefault();
        var site = siteId is long selected ? world.Set<SavedMiningSite>().Find(selected) : null;
        if (site is not null && site.ExpiresAt <= now && site.ReplaceAt is null)
        {
            Retire(world, site, site.ExpiresAt);
        }
        if (site is null || site.ReplaceAt <= now)
        {
            long id = Math.Max(MiningSiteRules.FirstSiteId, (world.Filter<SavedMiningSite>(_ => true).Max(s => (long?)s.Id) ?? 0) + 1);
            int prior =
                site is not null && wormholeClass > 0
                    ? Array.FindIndex(eligible, definition => definition.DungeonId == site.DungeonId)
                    : -1;
            long next = prior >= 0 ? prior + 1 : id - MiningSiteRules.FirstSiteId + systemId;
            var definition = eligible[(int)(next % eligible.Length)];
            double offset =
                CombatAnomalyRules.PlacementOffset
                * (
                    family == MiningFamily.Gas
                        ? GasPlacement
                        : IcePlacement + (id - MiningSiteRules.FirstSiteId) % IcePlacementSteps / IcePlacementScale
                );
            site = new()
            {
                Id = id,
                SystemId = systemId,
                Family = family,
                DungeonId = definition.DungeonId,
                X = anchor.X + offset,
                Y = anchor.Y - offset,
                Z = anchor.Z + offset,
                ExpiresAt = family == MiningFamily.Ice ? long.MaxValue : checked(now + MiningSiteRules.Lifetime),
            };
            world.Add(site);
            CreateDeposits(world, site, definition);
        }
        if (site.ReplaceAt is long replacement)
        {
            transaction?.Commit();
            return new(replacement, []);
        }
        var profile = MiningSiteCatalog.Sites.Single(s => s.DungeonId == site.DungeonId);
        long deadline = site.ExpiresAt;
        IReadOnlyList<StaticSpaceObject> scenery = [];
        bool guarded = false;
        bool started = true;
        if (
            profile.WormholeClasses is not null
            && SleeperSiteCatalog.Read(catalog).FirstOrDefault(s => s.DungeonId == site.DungeonId) is { } sleeper
        )
        {
            var encounter = CombatAnomalyStore.EnsureGuarded(
                world,
                catalog,
                site.Id,
                systemId,
                site.DungeonId,
                new(site.X, site.Y, site.Z)
            );
            deadline = Math.Min(deadline, CombatAnomalyStore.AdvanceGuarded(world, catalog, site.Id, now));
            scenery = CombatAnomalyStore.GuardedObjects(encounter, sleeper);
            guarded = true;
            started = world.Set<Eden.Data.World.Exploration.SavedSleeperSite>().Find(encounter.Id)!.ActivatedAt.HasValue;
        }
        var contact = new CombatAnomaly(
            site.Id,
            ScannerTargetIds.FromMiningSequence(site.Id - MiningSiteRules.FirstSiteId),
            systemId,
            site.DungeonId,
            profile.NameId,
            profile.Difficulty,
            new(site.X, site.Y, site.Z),
            started,
            scenery,
            FactionId: guarded ? SleeperRules.FactionId : 0,
            IsSignature: family == MiningFamily.Gas,
            Mining: site.Family
        );
        transaction?.Commit();
        return new(deadline, [contact]);
    }

    private static void CreateDeposits(WorldAction world, SavedMiningSite site, MiningSiteDefinition definition)
    {
        for (int index = 0; index < definition.Deposits.Count; index++)
        {
            var deposit = definition.Deposits[index];
            long id = checked(
                MiningSiteRules.FirstDepositId + (site.Id - MiningSiteRules.FirstSiteId) * MiningSiteRules.DepositsPerSite + index
            );
            if (!MiningSiteRules.IsDeposit(id))
            {
                throw new InvalidDataException("The mining deposit identifier range is exhausted.");
            }
            var position = new SpacePosition(site.X, site.Y, site.Z) + deposit.Position;
            world.Add(
                new WorldAsteroid
                {
                    Id = id,
                    SystemId = site.SystemId,
                    TypeId = deposit.TypeId,
                    Quantity = deposit.Quantity,
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                    Radius = deposit.Radius,
                }
            );
            world.Add(new SavedMiningDeposit { Id = id, SiteId = site.Id });
        }
    }

    internal static void Collected(WorldDbContext world, long depositId, long now) => Collected(new SqlWorldAction(world), depositId, now);
}
