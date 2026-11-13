using Eden.Data.Catalog.Mining;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

internal sealed record MiningSiteView(long Deadline, IReadOnlyList<CombatAnomaly> Sites);

[Injectable(InjectionType.Singleton)]
internal sealed partial class MiningSiteStore(WorldDatabase database) : IMiningSites
{
    // Keep resource sites apart from combat placements and vary replacement ice fields.
    private const int GasPlacement = 25;
    private const int IcePlacement = 26;
    private const int IcePlacementSteps = 97;
    private const double IcePlacementScale = 100;

    public IReadOnlyList<CombatAnomaly> Signatures(int systemId, long now) => Get(systemId, now, MiningFamily.Gas);

    public IReadOnlyList<CombatAnomaly> Anomalies(int systemId, long now) => Get(systemId, now, MiningFamily.Ice);

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

    private MiningSiteView Read(int systemId, long now, MiningFamily family)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
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
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var site = world
            .Set<SavedMiningSite>()
            .Where(s => s.SystemId == systemId && s.Family == family)
            .OrderByDescending(s => s.Id)
            .FirstOrDefault();
        if (site is not null && site.ExpiresAt <= now && site.ReplaceAt is null)
        {
            Retire(world, site, site.ExpiresAt);
        }
        if (site is null || site.ReplaceAt <= now)
        {
            long id = Math.Max(MiningSiteRules.FirstSiteId, (world.Set<SavedMiningSite>().Max(s => (long?)s.Id) ?? 0) + 1);
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
            world.SaveChanges();
        }
        if (site.ReplaceAt is long replacement)
        {
            world.SaveChanges();
            transaction.Commit();
            return new(replacement, []);
        }
        var profile = MiningSiteCatalog.Sites.Single(s => s.DungeonId == site.DungeonId);
        var contact = new CombatAnomaly(
            site.Id,
            ScannerTargetIds.FromMiningSequence(site.Id - MiningSiteRules.FirstSiteId),
            systemId,
            site.DungeonId,
            profile.NameId,
            profile.Difficulty,
            new(site.X, site.Y, site.Z),
            true,
            [],
            FactionId: 0,
            IsSignature: family == MiningFamily.Gas,
            Mining: site.Family
        );
        world.SaveChanges();
        transaction.Commit();
        return new(site.ExpiresAt, [contact]);
    }

    private static void CreateDeposits(WorldDbContext world, SavedMiningSite site, MiningSiteDefinition definition)
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

    internal static void Collected(WorldDbContext world, long depositId, long now)
    {
        if (!MiningSiteRules.IsDeposit(depositId) || world.Set<SavedMiningDeposit>().Find(depositId) is not { } member)
        {
            return;
        }
        var ids = world.Set<SavedMiningDeposit>().Where(d => d.SiteId == member.SiteId).Select(d => d.Id);
        if (!world.Set<WorldAsteroid>().Any(a => ids.Contains(a.Id) && a.Quantity > 0))
        {
            var site = world.Set<SavedMiningSite>().Find(member.SiteId)!;
            if (site.ReplaceAt is null)
            {
                Retire(world, site, now);
            }
        }
    }

    private static void Retire(WorldDbContext world, SavedMiningSite site, long now)
    {
        site.ReplaceAt = checked(
            now + (site.Family == MiningFamily.Ice ? MiningSiteRules.IceReplacementDelay : MiningSiteRules.ReplacementDelay)
        );
        var members = world.Set<SavedMiningDeposit>().Where(d => d.SiteId == site.Id).ToArray();
        var ids = members.Select(d => d.Id).ToArray();
        world.RemoveRange(world.Set<WorldAsteroid>().Where(a => ids.Contains(a.Id)).ToArray());
        world.RemoveRange(members);
    }
}
