using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;
using Eden.World.Alliances;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private readonly record struct SovereigntyAttackPlan(
        SovereigntyAttackObjective Objective,
        SovereigntyEntosisTarget Target,
        int HomeSystemId,
        CapsuleerTrafficRoute Route,
        long? ModuleId,
        long CaptureDuration,
        long EstimatedDuration,
        int FuelUnits
    );

    private bool TryAttackSovereignty(
        EconomyTransaction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        IReadOnlyList<SavedSovereigntyClaim> holdings,
        IReadOnlyList<SovereigntyExpedition> otherExpeditions,
        bool prepare
    )
    {
        if (!SovereigntyAttacksEnabled(world))
        {
            return false;
        }

        if (otherExpeditions.Any(expedition => expedition.Duty != SovereigntyDuty.Attack))
        {
            return false;
        }
        var home = holdings[0];
        int members = world.Filter<SavedAllianceMember>(member => member.AllianceId == home.AllianceId).Count();
        if (!CapsuleerAlliances.CanFund(world, corporation, members, 0))
        {
            return false;
        }

        var candidates = CapsuleerAlliances
            .NearbyClaimSites(catalog, Pilot.SystemId)
            .Select(system => world.Set<SavedSovereigntyClaim>().Find(system))
            .OfType<SavedSovereigntyClaim>()
            .SelectMany(claim =>
                AttackAnchor(world, catalog, home, claim, holdings) is int anchor
                    ? PilotEntosisStore.AttackTargets(world, claim, Now).Select(target => (Claim: claim, Target: target, Anchor: anchor))
                    : []
            )
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        var plans = AttackPlans()
            .OrderByDescending(plan => plan.Target.CommandNode)
            .ThenByDescending(plan => plan.ModuleId.HasValue)
            .ThenBy(plan => plan.EstimatedDuration)
            .ThenBy(plan => plan.FuelUnits)
            .ThenBy(plan => plan.Target.SystemId)
            .ThenBy(plan => plan.Target.Id);
        foreach (var plan in plans)
        {
            if (plan.ModuleId is not long module)
            {
                if (PrepareSovereigntyFit(world, catalog, corporation, home.AllianceId, plan.CaptureDuration))
                {
                    return true;
                }
                continue;
            }

            LaunchSovereignty(
                plan.Route,
                new(
                    home.AllianceId,
                    corporation.Id,
                    plan.Target.SystemId,
                    0,
                    module,
                    plan.Target.Id,
                    Duty: SovereigntyDuty.Attack,
                    HomeSystemId: plan.HomeSystemId
                )
                {
                    Attack = plan.Objective,
                }
            );
            return true;
        }
        return false;

        IEnumerable<SovereigntyAttackPlan> AttackPlans()
        {
            const int expeditionLink = CapsuleerAllianceRules.ExpeditionLink;
            bool canPrepare = prepare && EntosisCatalog.Supported(catalog, expeditionLink);
            var pilot = world.Pilots.Find(pilotId)!;
            var link = FittedEntosisModule(world, catalog, pilot);
            var fitted = link is null ? null : ExpeditionProfile(world, catalog, pilot, link.Id);
            long availableCycles = fitted is null ? 0 : ExpeditionFuel(world, pilot, fitted);
            EntosisProfile? standardProfile = null;
            var avoided = AvoidedSupplierSystems();
            Dictionary<(int System, HullClass Hull, int Jumps), CapsuleerTrafficRoute?> routes = [];
            foreach (var (rival, target, anchor) in candidates)
            {
                SovereigntyAttackObjective objective = new(rival.SystemId, rival.HubId, rival.AllianceId);
                if (
                    otherExpeditions.Count != 0
                    && (
                        !target.CommandNode
                        || otherExpeditions.Any(expedition =>
                            expedition.Attack != objective || !expedition.Returning && expedition.HubId == target.Id
                        )
                    )
                )
                {
                    continue;
                }

                long duration = PilotEntosisStore.AttackDuration(world, catalog, rival, target);
                bool ready = fitted is not null && availableCycles >= RequiredExpeditionCycles(duration, fitted);
                if (!ready && !canPrepare)
                {
                    continue;
                }
                var hull = ready ? (HullClass)Pilot.ShipGroupId : HullClass.Hauler;
                int jumps = target.CommandNode ? TrafficJourneyRules.MaximumJumps : CapsuleerAllianceRules.FirstClaimMaximumJumps;
                var key = (target.SystemId, hull, jumps);
                if (!routes.TryGetValue(key, out var route))
                {
                    route = CapsuleerTrafficRoutes.ToClaimSystem(
                        catalog,
                        Pilot.StationId,
                        target.SystemId,
                        hull,
                        avoided,
                        jumps,
                        policy: world.Settings.Capsuleers
                    );
                    routes.Add(key, route);
                }
                if (route is null)
                {
                    continue;
                }
                var profile = ready ? fitted! : standardProfile ??= EntosisCatalog.Module(catalog.FittingAttributes(expeditionLink), 1);
                // Allow a minute per leg for gate travel, final warp and lock before committing the launch.
                long leadTime = TimeSpan.FromMilliseconds(profile.Duration).Ticks + TimeSpan.TicksPerMinute * route.Systems.Length;
                if (PilotEntosisStore.AttackCanWarmUp(world, rival.SystemId, target.Id, Now, leadTime))
                {
                    int fuel = checked(RequiredExpeditionCycles(duration, profile) * profile.FuelQuantity);
                    yield return new(
                        objective,
                        target,
                        anchor,
                        route,
                        ready ? link!.Id : null,
                        duration,
                        checked(leadTime + duration),
                        fuel
                    );
                }
            }
        }
    }

    private bool CanContinueSovereigntyAttack(WorldAction world, CatalogDbContext catalog, SovereigntyExpedition expedition) =>
        SovereigntyAttacksEnabled(world)
        && expedition.HomeSystemId is int homeSystem
        && world.Set<SavedSovereigntyClaim>().Find(homeSystem) is { } home
        && home.AllianceId == expedition.AllianceId
        && expedition.Attack is { } attack
        && world.Set<SavedSovereigntyClaim>().Find(attack.SystemId) is { } rival
        && rival.HubId == attack.HubId
        && rival.AllianceId == attack.AllianceId
        && expedition.HubId is long target
        && AttackAnchor(world, catalog, home, rival).HasValue
        && PilotEntosisStore.AttackTargetAdmitted(world, rival, expedition.TargetSystemId, target, Now);

    private int? AttackAnchor(
        WorldAction world,
        CatalogDbContext catalog,
        SavedSovereigntyClaim home,
        SavedSovereigntyClaim rival,
        IReadOnlyList<SavedSovereigntyClaim>? holdings = null
    )
    {
        if (home.AllianceId == rival.AllianceId || world.Set<SavedAlliance>().Find(rival.AllianceId) is not { ClosedAt: null } alliance)
        {
            return null;
        }
        if (CorporationWarStore.HasActiveWar(world, Pilot.CorpId, rival.CorporationId, Now))
        {
            return home.SystemId;
        }
        double? standing = AllianceStore.Standing(world, home.AllianceId, rival.AllianceId);
        if (standing < 0)
        {
            return home.SystemId;
        }
        if (standing is not (null or 0) || world.Set<SavedCapsuleerAgent>().Find(alliance.CreatorId) is null)
        {
            return null;
        }

        var neighbors = CapsuleerTrafficRoutes.Neighbours(catalog, rival.SystemId);
        // Planning can choose any held border. A saved assignment revalidates its own border.
        if (holdings is null)
        {
            return neighbors.Contains(home.SystemId) ? home.SystemId : null;
        }
        return holdings.FirstOrDefault(claim => neighbors.Contains(claim.SystemId))?.SystemId;
    }

    private static bool SovereigntyAttacksEnabled(WorldAction world) =>
        world.Settings.Capsuleers is { NewSovereigntyClaims: true, RegionalConflicts: true };
}
