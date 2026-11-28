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
    private bool TryAttackSovereignty(
        EconomyTransaction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        SavedSovereigntyClaim home,
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
        int members = world.Filter<SavedAllianceMember>(member => member.AllianceId == home.AllianceId).Count();
        if (!CapsuleerAlliances.CanFund(world, corporation, members, 0))
        {
            return false;
        }

        var avoided = AvoidedSupplierSystems();
        var candidates = CapsuleerAlliances
            .NearbyClaimSites(catalog, Pilot.SystemId)
            .Select(system => world.Set<SavedSovereigntyClaim>().Find(system))
            .OfType<SavedSovereigntyClaim>()
            .Where(claim => IsSovereigntyRival(world, catalog, home, claim))
            .SelectMany(claim => PilotEntosisStore.AttackTargets(world, claim, Now).Select(target => (Claim: claim, Target: target)))
            .OrderByDescending(candidate => candidate.Target.CommandNode);
        foreach (var (rival, target) in candidates)
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
            long? module = ReadyEntosisModule(world, catalog, duration);
            if (module is null && (!prepare || !EntosisCatalog.Supported(catalog, CapsuleerAllianceRules.ExpeditionLink)))
            {
                continue;
            }
            var route = CapsuleerTrafficRoutes.ToClaimSystem(
                catalog,
                Pilot.StationId,
                target.SystemId,
                module.HasValue ? (HullClass)Pilot.ShipGroupId : HullClass.Hauler,
                avoided,
                target.CommandNode ? TrafficJourneyRules.MaximumJumps : CapsuleerAllianceRules.FirstClaimMaximumJumps,
                policy: world.Settings.Capsuleers
            );
            if (route is null)
            {
                continue;
            }
            var profile = module.HasValue
                ? ExpeditionProfile(world, catalog, world.Pilots.Find(pilotId)!, module.Value)
                : EntosisCatalog.Module(catalog.FittingAttributes(CapsuleerAllianceRules.ExpeditionLink), 1);
            // Allow a minute per leg for gate travel, final warp and lock before committing the launch.
            long leadTime = TimeSpan.FromMilliseconds(profile.Duration).Ticks + TimeSpan.TicksPerMinute * route.Systems.Length;
            if (!PilotEntosisStore.AttackCanWarmUp(world, rival.SystemId, target.Id, Now, leadTime))
            {
                continue;
            }
            if (!module.HasValue)
            {
                if (PrepareSovereigntyFit(world, catalog, corporation, home.AllianceId, duration))
                {
                    return true;
                }
                continue;
            }

            LaunchSovereignty(
                route,
                new(
                    home.AllianceId,
                    corporation.Id,
                    target.SystemId,
                    0,
                    module.Value,
                    target.Id,
                    Duty: SovereigntyDuty.Attack,
                    HomeSystemId: home.SystemId
                )
                {
                    Attack = objective,
                }
            );
            return true;
        }
        return false;
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
        && IsSovereigntyRival(world, catalog, home, rival)
        && PilotEntosisStore.AttackTargetAdmitted(world, rival, expedition.TargetSystemId, target, Now);

    private bool IsSovereigntyRival(WorldAction world, CatalogDbContext catalog, SavedSovereigntyClaim home, SavedSovereigntyClaim rival)
    {
        if (home.AllianceId == rival.AllianceId || world.Set<SavedAlliance>().Find(rival.AllianceId) is not { ClosedAt: null } alliance)
        {
            return false;
        }
        if (CorporationWarStore.HasActiveWar(world, Pilot.CorpId, rival.CorporationId, Now))
        {
            return true;
        }
        double? standing = AllianceStore.Standing(world, home.AllianceId, rival.AllianceId);
        return standing < 0
            || (standing is null or 0)
                && world.Set<SavedCapsuleerAgent>().Find(alliance.CreatorId) is not null
                && CapsuleerTrafficRoutes.Neighbours(catalog, home.SystemId).Contains(rival.SystemId);
    }

    private static bool SovereigntyAttacksEnabled(WorldAction world) =>
        world.Settings.Capsuleers is { NewSovereigntyClaims: true, RegionalConflicts: true };
}
