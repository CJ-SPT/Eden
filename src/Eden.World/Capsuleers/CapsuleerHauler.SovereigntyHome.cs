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
    private bool TryProtectSovereignty(
        EconomyTransaction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        SavedSovereigntyClaim home,
        bool prepare
    )
    {
        var avoided = AvoidedSupplierSystems();
        var hull = (HullClass)Pilot.ShipGroupId;
        foreach (var target in entosis.DefenseTargets(world, home, Now))
        {
            long duration = EntosisCatalog.Defense(catalog, target.CommandNode).Duration;
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
            if (!module.HasValue)
            {
                return prepare && PrepareSovereigntyFit(world, catalog, corporation, home.AllianceId, duration);
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
                    Duty: SovereigntyDuty.Defend,
                    HomeSystemId: home.SystemId
                )
            );
            return true;
        }
        if (home.CorporationId != corporation.Id)
        {
            return false;
        }
        var supplyRoute = CapsuleerTrafficRoutes.ToClaimSystem(
            catalog,
            Pilot.StationId,
            home.SystemId,
            hull,
            avoided,
            policy: world.Settings.Capsuleers
        );
        if (supplyRoute is null || ammunitionSupply.LoadSovereigntyReagent(world, catalog, Pilot, corporation, home, Now) is not { } load)
        {
            return false;
        }
        _operation.MarketChanged = true;
        LaunchSovereignty(
            supplyRoute,
            new(
                home.AllianceId,
                corporation.Id,
                home.SystemId,
                load.ItemId,
                0,
                home.HubId,
                Duty: SovereigntyDuty.ReagentDelivery,
                HomeSystemId: home.SystemId,
                CargoQuantity: load.Quantity
            )
        );
        return true;
    }

    private bool PrepareSovereigntyFit(
        EconomyTransaction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        int alliance,
        long duration
    )
    {
        if (!ammunitionSupply.OutfitExpedition(world, catalog, Pilot, corporation, alliance, duration, Now))
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
            _work = Work with { HomeDefenseAllianceId = null };
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
                    && live.HaulerCheckpoints.GetValueOrDefault(pilot.Id)?.Work is { } work
                    && work.ShipId == pilot.ActiveShipId
                    && work.HomeDefenseAllianceId == home.AllianceId
                )
        )
        {
            return false;
        }
        _work = Work with
        {
            HomeDefenseAllianceId = home.AllianceId,
            DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(HaulingRules.PollSeconds).Ticks,
            WaitingReason = "Standing by to defend the alliance's home system.",
        };
        return true;
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
            SovereigntyDuty.Claim when expedition.HubId is null => CanManageOwnedHub(world) && !PilotEntosisStore.HasHub(world, home),
            SovereigntyDuty.Claim => world.Set<SavedSovereigntyClaim>().Find(home) is null,
            SovereigntyDuty.Defend when expedition.HubId is long target => entosis.DefenseNeeded(
                world,
                home,
                expedition.AllianceId,
                expedition.TargetSystemId,
                target,
                Now
            ),
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
