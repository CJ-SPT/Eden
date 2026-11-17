using Eden.Data.Catalog.Mining;
using Eden.Data.World.Mining;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;
using Eden.World.Storage;

namespace Eden.World.Mining;

internal sealed partial class MiningSiteStore
{
    // Eden deposit calibration: eight rocks per tier, 10000 units per tier per rock; ten-minute replacement after depletion.
    private const int RocksPerTier = 8;
    private const int UnitsPerTier = 10000;
    private const int DepositSeparation = 15000;
    private const float DepositRadius = 1500;
    private const int SovereigntyPlacement = 100;

    private IReadOnlyList<CombatAnomaly> SovereigntyOres(int system, long now)
    {
        var live = LiveWorld.Find(database.Path);
        if (live is null)
        {
            return [];
        }
        using var ownership = live.Enter();
        var reads = LiveReadModels.Find(database.Path);
        return (
            reads?.MiningSites(
                system,
                MiningFamily.Ore,
                database.Catalog.Identity.Generation,
                now,
                () => ReadSovereigntyOres(live, system, now)
            )
            ?? ReadSovereigntyOres(live, system, now)
        ).Sites;
    }

    private MiningSiteView ReadSovereigntyOres(LiveWorld live, int system, long now)
    {
        using var action = live.Economy.Begin(new(systems: [system]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var upgrades = SovereigntySites.Read(action, catalog, system).Ores;
        var latest = action
            .Filter<SavedMiningSite>(row => row.SystemId == system && row.Family == MiningFamily.Ore)
            .Select(row => new { row.Id, row.DungeonId })
            .AsEnumerable()
            .GroupBy(row => row.DungeonId)
            .ToDictionary(group => group.Key, group => group.Max(row => row.Id));
        if (upgrades.Count == 0 && latest.Count == 0)
        {
            action.Commit();
            return new(long.MaxValue, []);
        }
        List<CombatAnomaly> contacts = [];
        long deadline = long.MaxValue;
        foreach (int type in latest.Keys.Union(upgrades.Select(row => row.UpgradeTypeId)).Order())
        {
            var upgrade = upgrades.SingleOrDefault(row => row.UpgradeTypeId == type);
            var site = latest.TryGetValue(type, out long prior) ? action.Set<SavedMiningSite>().Find(prior) : null;
            if (site is null || site.ReplaceAt <= now)
            {
                if (upgrade is null)
                {
                    continue;
                }
                var anchor = catalog.CelestialsInSystem(system).OrderBy(row => row.Id).First();
                long id = Math.Max(
                    MiningSiteRules.FirstSiteId,
                    (action.Set<SavedMiningSite>().Select(row => (long?)row.Id).Max() ?? 0) + 1
                );
                double offset = CombatAnomalyRules.PlacementOffset * (SovereigntyPlacement + type % 100);
                site = action
                    .Add(
                        new SavedMiningSite
                        {
                            Id = id,
                            SystemId = system,
                            Family = MiningFamily.Ore,
                            DungeonId = type,
                            X = anchor.X + offset,
                            Y = anchor.Y + offset,
                            Z = anchor.Z - offset,
                            ExpiresAt = long.MaxValue,
                        }
                    )
                    .Entity;
                var profile = new MiningSiteDefinition(
                    type,
                    upgrade.NameId,
                    "Sovereignty ore deposit",
                    MiningFamily.Ore,
                    upgrade.Tier,
                    [],
                    Enumerable
                        .Range(0, RocksPerTier * upgrade.Tier)
                        .Select(index => new MiningDepositDefinition(
                            upgrade.OreTypeId,
                            new(index * DepositSeparation, index % 2 * DepositSeparation, 0),
                            UnitsPerTier * upgrade.Tier,
                            DepositRadius
                        ))
                        .ToArray()
                );
                CreateDeposits(action, site, profile);
                long hub = action.Set<SavedSovereigntyClaim>().Find(system)!.HubId;
                action.Add(
                    new SavedSovereigntyMiningLink
                    {
                        SiteId = site.Id,
                        HubId = hub,
                        TypeId = type,
                    }
                );
            }
            if (site.ReplaceAt is long replacement)
            {
                if (upgrade is not null)
                {
                    deadline = Math.Min(deadline, replacement);
                }
                continue;
            }
            int oreName =
                upgrade?.NameId
                ?? checked(
                    (int)(
                        catalog
                            .Require(
                                CatalogData.Client.Types,
                                (long)
                                    action
                                        .Filter<SavedMiningDeposit>(row => row.SiteId == site.Id)
                                        .Select(row => row.Id)
                                        .Select(id => action.Set<WorldAsteroid>().Find(id)!.TypeId)
                                        .First()
                            )
                            .TypeNameID
                        ?? 0
                    )
                );
            contacts.Add(
                new(
                    site.Id,
                    ScannerTargetIds.FromMiningSequence(site.Id - MiningSiteRules.FirstSiteId),
                    system,
                    type,
                    oreName,
                    upgrade?.Tier ?? 1,
                    new(site.X, site.Y, site.Z),
                    true,
                    [],
                    FactionId: 0,
                    Mining: MiningFamily.Ore
                )
            );
        }
        action.Commit();
        return new(deadline, contacts.ToArray());
    }
}
