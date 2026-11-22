using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private sealed record SnapshotPvpFit(
        long KillId,
        ReplacementKit Kit,
        ReplacementPart[] Parts,
        bool DealsDamage,
        (int Stage, int AdvancedModules, int Modules) Progress
    );

    private sealed record SnapshotPvpFits(int[] HullTypes, IReadOnlyDictionary<int, SnapshotPvpFit[]> ByHull);

    private static readonly ConditionalWeakTable<TrafficSnapshot, ConcurrentDictionary<long, Lazy<SnapshotPvpFits>>> s_snapshotPvpFits =
        new();

    private CapsuleerPvpFitGoal? SelectSnapshotPvpGoal(
        EconomyTransaction world,
        CatalogDbContext catalog,
        TrafficSnapshot snapshot,
        DockedPilot pilot,
        int station,
        Ship? current,
        int stagingSystem,
        bool visitsHighSecurity
    )
    {
        if (snapshot.Profiles.Count == 0)
        {
            return null;
        }
        var fits = s_snapshotPvpFits
            .GetOrCreateValue(snapshot)
            .GetOrAdd(catalog.Definitions.Identity.Generation, _ => new(() => LoadSnapshotPvpFits(catalog, snapshot)))
            .Value;
        long id = pilot.PilotId;
        var policy = world.Settings.Capsuleers;
        bool canFlySupport =
            policy.RoamingGangs
            && PatrolRules.Allowed(
                new(PatrolKind.Roam, stagingSystem),
                catalog.Definition(CapsuleerProvisioningCatalog.Security, stagingSystem),
                policy
            );
        var previous = current is null
            ? default
            : PvpFitProgress(catalog, current.TypeId, world.FittedItems(id, current.Id).Select(item => item.TypeId));
        var hullOffers = market.SupplyOrders(world, catalog, station, fits.HullTypes, id);
        var candidates = hullOffers
            .Where(ask => StandardMarketRules.CanFill(1, ask.Remaining, ask.MinimumVolume))
            .Select(ask => ask.TypeId)
            .Distinct()
            .Where(type => PvpRouteAllows(catalog, type, visitsHighSecurity))
            .SelectMany(type => fits.ByHull[type])
            .Where(fit => (fit.DealsDamage || canFlySupport) && (current is null || fit.Progress.CompareTo(previous) > 0))
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }
        var types = candidates.SelectMany(fit => fit.Parts).Select(part => part.TypeId).Distinct().ToArray();
        var usable = PilotSkillReader.UsableTypes(world, catalog, id, types);
        var offers = hullOffers
            .Concat(market.SupplyOrders(world, catalog, station, types.Where(type => !fits.ByHull.ContainsKey(type)).ToArray(), id))
            .ToLookup(ask => ask.TypeId);
        Dictionary<ReplacementPart, MarketPurchase?> quotes = [];
        foreach (
            var fit in candidates
                .OrderByDescending(fit => fit.DealsDamage)
                .ThenByDescending(fit => current is null && fit.Parts.All(part => usable.Contains(part.TypeId)))
                .ThenBy(fit => fit.Progress)
                .ThenBy(fit => TravelerRoutineRules.Mix((ulong)id ^ TravelerRoutineRules.Mix((ulong)fit.KillId)))
        )
        {
            if (fit.Parts.All(part => Available(part)))
            {
                return new(fit.Kit.HullTypeId, fit.Kit.Slots, fit.Kit.Magazines);
            }
        }
        return null;

        bool Available(ReplacementPart part)
        {
            if (!quotes.TryGetValue(part, out var quote))
            {
                quotes[part] = quote = QuoteInput(offers[part.TypeId], part.TypeId, part.Quantity);
            }
            return quote?.Quantity == part.Quantity;
        }
    }

    private MarketPurchase[]? QuotePvpGoal(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilot,
        int station,
        ReplacementPart[] parts,
        long budget
    )
    {
        if (budget <= 0)
        {
            return null;
        }
        var offers = market
            .SupplyOrders(world, catalog, station, parts.Select(part => part.TypeId).ToArray(), pilot)
            .ToLookup(ask => ask.TypeId);
        List<MarketPurchase> purchases = [];
        decimal cost = 0;
        foreach (var part in parts)
        {
            var quote = QuoteInput(offers[part.TypeId], part.TypeId, part.Quantity);
            if (quote is null || quote.Quantity != part.Quantity)
            {
                return null;
            }
            cost += (decimal)quote.LimitPriceCents * quote.Quantity;
            if (cost > budget)
            {
                return null;
            }
            purchases.Add(quote);
        }
        return purchases.ToArray();
    }

    private static bool PvpRouteAllows(CatalogDbContext catalog, int type, bool visitsHighSecurity) =>
        !visitsHighSecurity || TrafficRules.RouteAllows((HullClass)catalog.RequireItem(type).GroupId, true);

    private static (int Stage, int AdvancedModules, int Modules) PvpFitProgress(
        CatalogDbContext catalog,
        int hull,
        IEnumerable<int> modules
    )
    {
        var types = modules.ToArray();
        return (
            CapsuleerWorkHulls.Stage(catalog, hull, CapsuleerProfession.Combat),
            types.Count(type => catalog.Read(CatalogData.Client.Types, (long)type)?.TechLevel >= 2),
            types.Length
        );
    }

    private static SnapshotPvpFits LoadSnapshotPvpFits(CatalogDbContext catalog, TrafficSnapshot snapshot)
    {
        var byHull = snapshot
            .Profiles.DistinctBy(TrafficSnapshot.Signature)
            .Select(profile => (Fit: SnapshotPvpKit(catalog, profile), Profile: profile))
            .Where(candidate => candidate.Fit is not null)
            // A fit stays the same when only its remaining ammunition differed at the time of loss.
            .DistinctBy(candidate => TrafficSnapshot.ValidationSignature(candidate.Profile))
            .Select(candidate => candidate.Fit!)
            .GroupBy(fit => fit.Kit.HullTypeId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        return new(byHull.Keys.ToArray(), byHull);
    }

    private static SnapshotPvpFit? SnapshotPvpKit(CatalogDbContext catalog, TrafficProfile profile)
    {
        if (
            profile.Equipment.Length == 0
            || catalog.FittingType(profile.HullTypeId) is not { } hull
            || TrafficRules.LightHull((HullClass)hull.GroupId)
            || !CapsuleerPatrolFit.CombatHull(catalog, profile.HullTypeId)
        )
        {
            return null;
        }
        try
        {
            TrafficFitting.CheckTypes(catalog, profile);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        bool IsCharge(TrafficEquipment item) => catalog.FittingType(item.TypeId)?.CategoryId == (int)InventoryCategory.Charge;
        var slots = profile.Equipment.Where(item => !IsCharge(item)).Select(item => new FittingSlot(item.Flag, item.TypeId)).ToArray();
        var effects = slots.ToDictionary(
            slot => slot.FlagId,
            slot => FittingCatalog.DefaultEffect(catalog, slot.TypeId) ?? DogmaEffect.Online
        );
        if (effects.ContainsValue(DogmaEffect.MiningLaser))
        {
            return null;
        }

        List<ReplacementMagazine> magazines = [];
        bool dealsDamage = slots.Any(slot => slot.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun);
        foreach (var charge in profile.Equipment.Where(IsCharge))
        {
            int module = slots.Single(slot => slot.FlagId == charge.Flag).TypeId;
            if (
                !PilotAmmunitionStore.CompatibleCharge(catalog, module, charge.TypeId)
                || Magazine(catalog, charge.Flag, module, charge.TypeId) is not { } magazine
            )
            {
                return null;
            }
            magazines.Add(magazine);
            dealsDamage |=
                (effects[charge.Flag] is DogmaEffect.LaserFired or DogmaEffect.ProjectileFired or DogmaEffect.UseMissiles)
                && catalog.FittingType(module)?.GroupId != InterdictionRules.LauncherGroup;
        }
        if (!dealsDamage && !effects.Values.Any(PvpSupport.Supported))
        {
            return null;
        }

        ReplacementKit kit = new(profile.HullTypeId, slots, effects.Keys.ToHashSet(), [], magazines.ToArray());
        var parts = kit.Parts();
        return SupportedKit(catalog, kit, parts)
            ? new(profile.KillId, kit, parts, dealsDamage, PvpFitProgress(catalog, kit.HullTypeId, slots.Select(slot => slot.TypeId)))
            : null;
    }
}
