using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Sovereignty;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool TryDefendSovereignty(
        EconomyTransaction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        int alliance,
        IReadOnlyList<SavedSovereigntyClaim> holdings,
        IReadOnlyList<SovereigntyExpedition> active,
        bool prepare,
        out bool defenseNeeded
    )
    {
        defenseNeeded = false;
        var avoided = AvoidedSupplierSystems();
        var hull = (HullClass)Pilot.ShipGroupId;
        var reserved = active
            .Where(expedition => expedition is { Duty: SovereigntyDuty.Defend, Returning: false } && expedition.AllianceId == alliance)
            .Select(expedition => expedition.HubId)
            .ToHashSet();
        foreach (var objective in entosis.DefenseTargets(world, holdings, Now))
        {
            var target = objective.Target;
            if (reserved.Contains(target.Id))
            {
                continue;
            }
            // Keep the usual fuel budget for a fresh fight. A nearly lost node can need two scoring durations.
            long duration = (long)
                Math.Ceiling(EntosisCatalog.Defense(catalog, target.CommandNode).Duration * Math.Max(1, 1 - objective.TargetProgress));
            long? module = ReadyEntosisModule(world, catalog, duration);
            var route = CapsuleerTrafficRoutes.ToClaimSystem(
                catalog,
                Pilot.StationId,
                target.SystemId,
                module.HasValue ? hull : HullClass.Hauler,
                avoided,
                TrafficJourneyRules.MaximumJumps,
                policy: world.Settings.Capsuleers
            );
            if (route is null)
            {
                continue;
            }
            defenseNeeded = true;
            if (!module.HasValue)
            {
                bool keepFit = Pilot.ShipTypeId == CapsuleerAllianceRules.ExpeditionSquall;
                if (prepare && PrepareSovereigntyFit(world, catalog, corporation, alliance, duration, keepFit))
                {
                    return true;
                }
                continue;
            }
            LaunchSovereignty(
                route,
                new(
                    alliance,
                    corporation.Id,
                    target.SystemId,
                    0,
                    module.Value,
                    target.Id,
                    Duty: SovereigntyDuty.Defend,
                    HomeSystemId: objective.ClaimSystemId
                )
            );
            return true;
        }
        return false;
    }

    private bool TryResupplySovereignty(
        EconomyTransaction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        IReadOnlyList<SavedSovereigntyClaim> holdings
    )
    {
        var avoided = AvoidedSupplierSystems();
        foreach (var claim in holdings)
        {
            if (claim.CorporationId != corporation.Id || !CapsuleerIndustry.HasConfiguredSovereigntyUpgrades(world, claim.HubId))
            {
                continue;
            }
            var route = CapsuleerTrafficRoutes.ToClaimSystem(
                catalog,
                Pilot.StationId,
                claim.SystemId,
                (HullClass)Pilot.ShipGroupId,
                avoided,
                CapsuleerAllianceRules.FirstClaimMaximumJumps,
                policy: world.Settings.Capsuleers
            );
            if (route is null || ammunitionSupply.LoadSovereigntyReagent(world, catalog, Pilot, corporation, claim, Now) is not { } load)
            {
                continue;
            }
            _operation.MarketChanged = true;
            LaunchSovereignty(
                route,
                new(
                    claim.AllianceId,
                    corporation.Id,
                    claim.SystemId,
                    load.ItemId,
                    0,
                    claim.HubId,
                    Duty: SovereigntyDuty.ReagentDelivery,
                    HomeSystemId: claim.SystemId,
                    CargoQuantity: load.Quantity
                )
            );
            return true;
        }
        return false;
    }

    private bool PrepareSovereigntyFit(
        EconomyTransaction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        int alliance,
        long duration,
        bool useCurrentFit = false
    )
    {
        var pilot = world.Pilots.Find(pilotId)!;
        var profile =
            useCurrentFit && FittedEntosisModule(world, catalog, pilot) is { } link
                ? ExpeditionProfile(world, catalog, pilot, link.Id)
                : null;
        var outfit = ammunitionSupply.OutfitExpedition(world, catalog, Pilot, corporation, alliance, duration, Now, profile);
        _operation.MarketChanged |= outfit.MarketChanged;
        if (!outfit.Ready)
        {
            return false;
        }
        _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
        _navigation.AdoptHull();
        _work = Work with { ShipId = Pilot.ShipId };
        _operation.MarketChanged = true;
        _operation.TravelChanged = true;
        _ = TrySovereigntyExpedition(prepare: false);
        return true;
    }

    private long? ReadyEntosisModule(WorldAction world, CatalogDbContext catalog, long duration)
    {
        var pilot = world.Pilots.Find(pilotId)!;
        var module = FittedEntosisModule(world, catalog, pilot);
        if (module is null)
        {
            return null;
        }
        var profile = ExpeditionProfile(world, catalog, pilot, module.Id);
        return ExpeditionFuel(world, pilot, profile) >= RequiredExpeditionCycles(duration, profile) ? module.Id : null;
    }

    private static OwnedItem? FittedEntosisModule(WorldAction world, CatalogDbContext catalog, Pilot pilot) =>
        catalog.FittingType(pilot.ActiveShip.TypeId)?.GroupId == (int)HullClass.Interceptor
            ? null
            : world
                .Filter<OwnedItem>(row => row.LocationId == pilot.ActiveShipId)
                .AsEnumerable()
                .FirstOrDefault(row =>
                    row.OwnerId == pilot.Id
                    && row.Online
                    && row.Singleton
                    && InventoryDefinitions.IsHighSlot(row.FlagId)
                    && EntosisCatalog.Supported(catalog, row.TypeId)
                );

    private bool StandByAtSovereignty(
        EconomyTransaction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        IReadOnlyList<SavedSovereigntyClaim> holdings,
        bool prepare
    )
    {
        var pilot = world.Pilots.Find(pilotId)!;
        if (Live is not { } live || FittedEntosisModule(world, catalog, pilot) is not { } link)
        {
            ClearSovereigntyStandby();
            return false;
        }
        int alliance = holdings[0].AllianceId;
        long duration = EntosisCatalog.Defense(catalog, false).Duration;
        if (
            world.Filter<Pilot>(member => member.CorporationId == corporation.Id).AsEnumerable().Any(CoversStandby)
            || !ReachesHolding(pilot, Work)
        )
        {
            ClearSovereigntyStandby();
            return false;
        }
        var profile = ExpeditionProfile(world, catalog, pilot, link.Id);
        if (ExpeditionFuel(world, pilot, profile) < RequiredExpeditionCycles(duration, profile))
        {
            var outfit = prepare
                ? ammunitionSupply.OutfitExpedition(world, catalog, Pilot, corporation, alliance, duration, Now, profile)
                : default;
            _operation.MarketChanged |= outfit.MarketChanged;
            if (!outfit.Ready)
            {
                ClearSovereigntyStandby();
                return false;
            }
        }
        _work = Work with
        {
            HomeDefenseAllianceId = alliance,
            DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(HaulingRules.PollSeconds).Ticks,
            WaitingReason = "Standing by to defend alliance territory.",
        };
        return true;

        bool CoversStandby(Pilot member)
        {
            if (
                member.Id == pilotId
                || member.ActiveShip.Position is not null
                || member.ActiveShip.DockedLocationId != Pilot.StationId
                || world.Set<SavedCapsuleerSuspension>().Find(member.Id) is not null
                || HaulerCheckpoint.Read(world, live, member.Id) is not { SovereigntyExpedition: null, CorporationSupply: null } work
                || work.ShipId != member.ActiveShipId
                || work.HomeDefenseAllianceId != alliance
                || FittedEntosisModule(world, catalog, member) is not { } module
            )
            {
                return false;
            }
            var fitted = ExpeditionProfile(world, catalog, member, module.Id);
            return ExpeditionFuel(world, member, fitted) >= RequiredExpeditionCycles(duration, fitted) && ReachesHolding(member, work);
        }

        bool ReachesHolding(Pilot member, HaulerProgress work)
        {
            if (catalog.FittingType(member.ActiveShip.TypeId) is not { } hull)
            {
                return false;
            }
            var avoided = AvoidedSupplierSystems(work);
            return holdings.Any(claim =>
                CapsuleerTrafficRoutes.ToClaimSystem(
                    catalog,
                    Pilot.StationId,
                    claim.SystemId,
                    (HullClass)hull.GroupId,
                    avoided,
                    TrafficJourneyRules.MaximumJumps,
                    policy: world.Settings.Capsuleers
                )
                    is not null
            );
        }
    }

    private void ClearSovereigntyStandby()
    {
        if (Work.HomeDefenseAllianceId is not null)
        {
            _work = Work with { HomeDefenseAllianceId = null };
        }
    }

    private bool CanContinueSovereignty(WorldAction world, SovereigntyExpedition expedition)
    {
        if (!HasExpeditionAuthority(world, expedition))
        {
            return false;
        }
        int home = expedition.HomeSystemId ?? expedition.TargetSystemId;
        return expedition.Duty switch
        {
            SovereigntyDuty.Claim when expedition.HubId is null => CanManageOwnedHub(world)
                && !PilotEntosisStore.HasHub(world, expedition.TargetSystemId)
                && (
                    expedition.HomeSystemId is null
                    || world.Set<SavedSovereigntyClaim>().Find(home)?.AllianceId == expedition.AllianceId
                        && CapsuleerAlliances.CanRecoverClaim(world, expedition.TargetSystemId, expedition.AllianceId)
                ),
            SovereigntyDuty.Claim => world.Set<SavedSovereigntyClaim>().Find(expedition.TargetSystemId) is null,
            SovereigntyDuty.Defend when expedition.HubId is long target => entosis.DefenseNeeded(
                world,
                home,
                expedition.AllianceId,
                expedition.TargetSystemId,
                target,
                Now
            ),
            SovereigntyDuty.Attack => CanContinueSovereigntyAttack(world, _operation.Catalog, expedition),
            SovereigntyDuty.ReagentDelivery => world.Set<SavedSovereigntyClaim>().Find(home) is { } claim
                && claim.HubId == expedition.HubId
                && claim.AllianceId == expedition.AllianceId
                && claim.CorporationId == Pilot.CorpId
                && CanManageOwnedHub(world),
            _ => false,
        };
    }

    private bool CanManageOwnedHub(WorldAction world) =>
        world.Set<SavedCorporation>().Find(Pilot.CorpId) is { ClosedAt: null } corporation
        && CorporationRoleStore.Has(world, pilotId, corporation, CorporationRole.StationManager);

    private void DeliverSovereigntyReagent(SovereigntyExpedition expedition, StaticSpaceObject target)
    {
        if (
            Pilot.Position!.DistanceTo(target.Position) - Pilot.Flight.ShipRadius - target.Radius
            > SovereigntyResourceRules.InteractionRange
        )
        {
            _target = target;
            _work = Work with { Activity = HaulerActivity.Flying };
            Follow();
            return;
        }
        var world = _operation.World;
        if (InventoryPersistence.OwnedStack(world, pilotId, expedition.CargoId) is { } cargo && cargo.LocationId == Pilot.ShipId)
        {
            SovereigntyResourceStore.AddFuel(
                world,
                _operation.Catalog,
                pilotId,
                target.Id,
                cargo.Id,
                Math.Min(cargo.Quantity, expedition.CargoQuantity),
                Now
            );
        }
        ReturnFromSovereignty(expedition with { CargoId = 0, CargoQuantity = 0 });
    }
}
