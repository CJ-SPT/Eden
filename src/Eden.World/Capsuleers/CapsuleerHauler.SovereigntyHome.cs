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
                if (prepare && PrepareSovereigntyFit(world, catalog, corporation, alliance, duration))
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
        long duration
    )
    {
        var outfit = ammunitionSupply.OutfitExpedition(world, catalog, Pilot, corporation, alliance, duration, Now);
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
        var module = FittedEntosisModule(world, catalog);
        if (module is null)
        {
            return null;
        }
        var profile = ExpeditionProfile(world, catalog, module.Id);
        return ExpeditionFuel(world, profile) >= RequiredExpeditionCycles(duration, profile) ? module.Id : null;
    }

    private OwnedItem? FittedEntosisModule(WorldAction world, CatalogDbContext catalog) =>
        (HullClass)Pilot.ShipGroupId == HullClass.Interceptor
            ? null
            : world
                .Filter<OwnedItem>(row => row.LocationId == Pilot.ShipId)
                .AsEnumerable()
                .FirstOrDefault(row =>
                    row.OwnerId == pilotId
                    && row.Online
                    && row.Singleton
                    && InventoryDefinitions.IsHighSlot(row.FlagId)
                    && EntosisCatalog.Supported(catalog, row.TypeId)
                );

    private bool StandByAtSovereignty(WorldAction world, CatalogDbContext catalog, SavedCorporation corporation, SavedSovereigntyClaim home)
    {
        if (Live is not { } live || FittedEntosisModule(world, catalog) is null)
        {
            ClearSovereigntyStandby();
            return false;
        }
        if (
            Work.HomeDefenseAllianceId != home.AllianceId
            && world
                .Filter<Pilot>(pilot => pilot.CorporationId == corporation.Id)
                .AsEnumerable()
                .Any(pilot =>
                    pilot.Id != pilotId
                    && world.Set<SavedCapsuleerSuspension>().Find(pilot.Id) is null
                    && pilot.ActiveShip.Position is null
                    && pilot.ActiveShip.DockedLocationId == Pilot.StationId
                    && HaulerCheckpoint.Read(world, live, pilot.Id) is { SovereigntyExpedition: null } work
                    && work.ShipId == pilot.ActiveShipId
                    && work.HomeDefenseAllianceId == home.AllianceId
                )
        )
        {
            ClearSovereigntyStandby();
            return false;
        }
        _work = Work with
        {
            HomeDefenseAllianceId = home.AllianceId,
            DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(HaulingRules.PollSeconds).Ticks,
            WaitingReason = "Standing by to defend alliance territory.",
        };
        return true;
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
