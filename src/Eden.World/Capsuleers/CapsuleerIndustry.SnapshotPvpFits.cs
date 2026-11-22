using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Eden.Data.Catalog.Capsuleers;
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
    private sealed record SnapshotPvpFit(long KillId, ReplacementKit Kit, ReplacementPart[] Parts, bool DealsDamage);

    private sealed record SnapshotPvpFits(int[] HullTypes, IReadOnlyDictionary<int, SnapshotPvpFit[]> ByHull);

    private static readonly ConditionalWeakTable<TrafficSnapshot, ConcurrentDictionary<long, Lazy<SnapshotPvpFits>>> s_snapshotPvpFits =
        new();

    private (ReplacementKit Kit, MarketPurchase[] Purchases)? QuoteSnapshotPvpFit(
        EconomyTransaction world,
        CatalogDbContext catalog,
        TrafficSnapshot snapshot,
        DockedPilot pilot,
        int station,
        long budget,
        int? stagingSystem = null,
        bool visitsHighSecurity = false
    )
    {
        if (budget <= 0 || snapshot.Profiles.Count == 0)
        {
            return null;
        }

        var fits = s_snapshotPvpFits
            .GetOrCreateValue(snapshot)
            .GetOrAdd(catalog.Definitions.Identity.Generation, _ => new(() => LoadSnapshotPvpFits(catalog, snapshot)))
            .Value;
        long id = pilot.PilotId;
        var hullTypes = visitsHighSecurity
            ? fits.HullTypes.Where(type => TrafficRules.RouteAllows((HullClass)catalog.RequireItem(type).GroupId, true)).ToArray()
            : fits.HullTypes;
        var usableHulls = PilotSkillReader.UsableTypes(world, catalog, id, hullTypes);
        var hullOffers = market.SupplyOrders(world, catalog, station, usableHulls.ToArray(), id);
        var policy = world.Settings.Capsuleers;
        bool canFlySupport =
            policy.RoamingGangs
            && PatrolRules.Allowed(
                new(PatrolKind.Roam, stagingSystem ?? pilot.SystemId),
                catalog.Definition(CapsuleerProvisioningCatalog.Security, stagingSystem ?? pilot.SystemId),
                policy
            );
        var candidates = hullOffers
            .Where(ask => ask.PriceCents < budget && StandardMarketRules.CanFill(1, ask.Remaining, ask.MinimumVolume))
            .Select(ask => ask.TypeId)
            .Distinct()
            .SelectMany(type => fits.ByHull[type])
            .Where(fit => fit.DealsDamage || canFlySupport)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var equipment = candidates.SelectMany(fit => fit.Parts).Select(part => part.TypeId).Where(type => !usableHulls.Contains(type));
        var usableEquipment = PilotSkillReader.UsableTypes(world, catalog, id, equipment.Distinct().ToArray());
        var viable = candidates
            .Where(fit => fit.Parts.All(part => usableHulls.Contains(part.TypeId) || usableEquipment.Contains(part.TypeId)))
            .ToArray();
        var requested = viable
            .SelectMany(fit => fit.Parts)
            .Select(part => part.TypeId)
            .Where(type => usableEquipment.Contains(type))
            .Distinct()
            .ToArray();
        var offers = hullOffers.Concat(market.SupplyOrders(world, catalog, station, requested, id)).ToLookup(ask => ask.TypeId);
        Dictionary<ReplacementPart, MarketPurchase?> quotes = [];
        List<SnapshotPvpFit> affordable = [];
        foreach (var fit in viable)
        {
            decimal cost = 0;
            foreach (var part in fit.Parts)
            {
                if (!quotes.TryGetValue(part, out var quote))
                {
                    quotes[part] = quote = QuoteInput(offers[part.TypeId], part.TypeId, part.Quantity);
                }
                if (quote is null || quote.Quantity != part.Quantity)
                {
                    cost = decimal.MaxValue;
                    break;
                }
                cost += (decimal)quote.LimitPriceCents * quote.Quantity;
                if (cost > budget)
                {
                    break;
                }
            }
            if (cost <= budget)
            {
                affordable.Add(fit);
            }
        }

        // Different pilots use different affordable fits without changing their preference on every station review.
        var ranked = affordable
            .OrderByDescending(fit => fit.DealsDamage)
            .ThenBy(fit => TravelerRoutineRules.Mix((ulong)id ^ TravelerRoutineRules.Mix((ulong)fit.KillId)));
        foreach (var fit in ranked)
        {
            if (
                CanReplaceKit(world, catalog, id, CapsuleerCareer.Hauling, fit.Kit, fit.Parts)
                && PilotFittingStore.CanOnlineFit(world, catalog, id, fit.Kit)
            )
            {
                return (fit.Kit, fit.Parts.Select(part => quotes[part]!).ToArray());
            }
        }
        return null;
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
        return SupportedKit(catalog, kit, parts) ? new(profile.KillId, kit, parts, dealsDamage) : null;
    }
}
