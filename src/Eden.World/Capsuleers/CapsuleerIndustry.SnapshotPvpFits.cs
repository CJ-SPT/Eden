using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
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

    private sealed record SnapshotPvpFits(
        int[] HullTypes,
        IReadOnlyDictionary<int, SnapshotPvpFit[]> ByHull,
        IReadOnlyDictionary<int, (int Skill, double Level)[]> Requirements,
        IReadOnlyDictionary<int, double> SkillRanks
    );

    private static readonly ConditionalWeakTable<TrafficSnapshot, ConcurrentDictionary<long, Lazy<SnapshotPvpFits>>> s_snapshotPvpFits =
        new();

    internal static void PrepareSnapshotPvpFits(CatalogDbContext catalog, TrafficSnapshot snapshot)
    {
        if (snapshot.Profiles.Count == 0)
        {
            return;
        }
        try
        {
            _ = ReadSnapshotPvpFits(catalog, snapshot);
        }
        catch (InvalidDataException)
        {
            // A bad fit index should only affect jobs that actually need it.
        }
    }

    private static SnapshotPvpFits ReadSnapshotPvpFits(CatalogDbContext catalog, TrafficSnapshot snapshot) =>
        s_snapshotPvpFits
            .GetOrCreateValue(snapshot)
            .GetOrAdd(catalog.Definitions.Identity.Generation, _ => new(() => LoadSnapshotPvpFits(catalog, snapshot)))
            .Value;

    private CapsuleerPvpFitGoal? SelectSnapshotPvpGoal(
        EconomyTransaction world,
        CatalogDbContext catalog,
        TrafficSnapshot snapshot,
        DockedPilot pilot,
        int station,
        Ship? current,
        int stagingSystem,
        bool visitsHighSecurity,
        Func<int, IEnumerable<int>, bool>? allowedMilitiaFit = null,
        long? affordableReadyBudget = null
    )
    {
        if (snapshot.Profiles.Count == 0)
        {
            return null;
        }
        using var phase = SessionPerformance.Phase("traffic.selectSnapshotPvpGoal");
        var fits = ReadSnapshotPvpFits(catalog, snapshot);
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
            .Where(fit => allowedMilitiaFit is null || allowedMilitiaFit(fit.Kit.HullTypeId, fit.Kit.Slots.Select(slot => slot.TypeId)))
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }
        var types = candidates.SelectMany(fit => fit.Parts).Select(part => part.TypeId).Distinct().ToArray();
        // Upgrades with no ready-fit requirement sort by progress regardless of current skill levels.
        var usable = current is null || affordableReadyBudget.HasValue ? UsableSnapshotTypes(world, id, fits, types) : null;
        var offers = hullOffers
            .Concat(market.SupplyOrders(world, catalog, station, types.Where(type => !fits.ByHull.ContainsKey(type)).ToArray(), id))
            .ToLookup(ask => ask.TypeId);
        Dictionary<ReplacementPart, MarketPurchase?> quotes = [];
        foreach (
            var fit in candidates
                .OrderByDescending(fit => fit.DealsDamage)
                .ThenByDescending(fit => current is null && fit.Parts.All(part => usable!.Contains(part.TypeId)))
                .ThenBy(fit => fit.Progress)
                .ThenBy(fit => TravelerRoutineRules.Mix((ulong)id ^ TravelerRoutineRules.Mix((ulong)fit.KillId)))
        )
        {
            if (
                fit.Parts.All(part => Available(part))
                && (
                    affordableReadyBudget is not long budget
                    || fit.Parts.Sum(part => (decimal)quotes[part]!.LimitPriceCents * part.Quantity) <= budget
                        && fit.Parts.All(part => usable!.Contains(part.TypeId))
                        && PvpFitQualified(world, catalog, pilot, fit.Kit, fit.Parts, out _)
                )
            )
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

    private static HashSet<int> UsableSnapshotTypes(EconomyTransaction world, long pilot, SnapshotPvpFits fits, int[] types)
    {
        Dictionary<int, int> levels = [];
        return types.Where(type => !fits.Requirements[type].Any(requirement => Level(requirement.Skill) < requirement.Level)).ToHashSet();

        int Level(int skill)
        {
            if (!levels.TryGetValue(skill, out int level))
            {
                level = new TrainedSkill(skill, world.SkillPoints(pilot, skill) ?? 0, fits.SkillRanks[skill]).Level;
                levels.Add(skill, level);
            }
            return level;
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
        using var phase = SessionPerformance.Phase("traffic.loadSnapshotPvpFits");
        var hulls = snapshot
            .Profiles.Where(profile => profile.Equipment.Length != 0)
            .Select(profile => profile.HullTypeId)
            .Distinct()
            .Where(hull => SnapshotPvpHull(catalog, hull))
            .ToHashSet();
        var profiles = snapshot.Profiles.Where(profile => profile.Equipment.Length != 0 && hulls.Contains(profile.HullTypeId)).ToArray();
        int[] typeIds = profiles.SelectMany(profile => profile.Equipment.Select(item => item.TypeId)).Concat(hulls).Distinct().ToArray();
        long[] catalogKeys = Array.ConvertAll(typeIds, type => (long)type);
        var definitions = catalog.Definitions;
        _ = definitions.FindMany(CatalogData.Client.Types, catalogKeys);
        _ = definitions.FindMany(CatalogData.Client.Typedogma, catalogKeys);
        _ = definitions.AttributeSets(typeIds);

        HashSet<string> validated = [];
        List<SnapshotPvpFit> fits = [];
        foreach (var profile in profiles.DistinctBy(TrafficSnapshot.Signature))
        {
            // Keep the first valid loadout when only the quantity of remaining ammunition differs.
            string signature = TrafficSnapshot.ValidationSignature(profile);
            if (!validated.Contains(signature) && SnapshotPvpKit(catalog, profile) is { } fit)
            {
                validated.Add(signature);
                fits.Add(fit);
            }
        }
        var byHull = fits.GroupBy(fit => fit.Kit.HullTypeId).ToDictionary(group => group.Key, group => group.ToArray());
        var requirements = byHull
            .Values.SelectMany(fits => fits)
            .SelectMany(fit => fit.Parts)
            .Select(part => part.TypeId)
            .Distinct()
            .ToDictionary(type => type, type => SnapshotSkillRequirements(catalog, type));
        var ranks = requirements
            .Values.SelectMany(requirements => requirements)
            .Select(requirement => requirement.Skill)
            .Distinct()
            .ToDictionary(
                skill => skill,
                skill => catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1)
            );
        return new(byHull.Keys.ToArray(), byHull, requirements, ranks);
    }

    private static (int Skill, double Level)[] SnapshotSkillRequirements(CatalogDbContext catalog, int type)
    {
        var attributes = catalog.FittingAttributes(type);
        return InventoryDefinitions
            .RequiredSkills.Select(pair =>
                (Skill: checked((int)attributes.GetValueOrDefault(pair.Skill)), Level: attributes.GetValueOrDefault(pair.Level))
            )
            .Where(requirement => requirement.Skill != 0)
            .ToArray();
    }

    private static bool SnapshotPvpHull(CatalogDbContext catalog, int type) =>
        catalog.FittingType(type) is { } hull
        && !TrafficRules.LightHull((HullClass)hull.GroupId)
        && CapsuleerPatrolFit.CombatHull(catalog, type);

    private static SnapshotPvpFit? SnapshotPvpKit(CatalogDbContext catalog, TrafficProfile profile)
    {
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
