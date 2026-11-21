using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool FreshCombat =>
        database.Settings.FreshUniverse
        && Pilot.ShipGroupId == (int)HullClass.Corvette
        && Work.CareerPlan?.Profession == CapsuleerProfession.Combat;

    private void WorkFreshCareer(EconomyTransaction world, CombatAnomaly? scoutedAnomaly)
    {
        var catalog = _operation.Catalog;
        if (Pilot.InStructure || world.Set<SavedCapsuleerAgent>().Find(pilotId) is not { } agent)
        {
            Wait("Waiting at a supported station.");
            return;
        }
        if (Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
        {
            _pilot = fleet.RecoverCorvette(world, catalog, pilotId, Pilot.ShipId, Pilot.DockedLocationId).Pilot;
            ResetTradeHull();
            Wait("Boarded the recovery corvette.");
            return;
        }

        var profession = FreshCareerRules.Profession(agent.Slot);
        bool rookie = Pilot.ShipGroupId == (int)HullClass.Corvette;
        var courier =
            profession == CapsuleerProfession.Hauling
                ? FreshCareerRoutes.Read(catalog, agent.CommunitySystemId, world.Settings.Capsuleers)
                : null;
        if (!rookie)
        {
            if (courier is not null && TryFreshDelivery(world, courier, startNew: false))
            {
                return;
            }
            JoinCorporationAtDock();
            if (TryCorporationRelocation() || TryCorporationSupply())
            {
                return;
            }
        }

        var training = rookie
            ? careerTraining.AdvanceFreshCareer(world, catalog, Pilot, profession, Now)
            : careerTraining.Advance(world, catalog, Pilot, profession, Work.CareerPlan, Now);
        _work = Work with { CareerPlan = training.Plan };
        _operation.MarketChanged |= training.MarketChanged;
        if (training.LevelChanged)
        {
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
        }
        if (
            (rookie && profession == CapsuleerProfession.Combat)
            || !world
                .Filter<OwnedItem>(item => item.LocationId == Pilot.ShipId)
                .Any(item =>
                    item.FlagId == (int)InventoryFlag.Cargo && catalog.RequireItem(item.TypeId).CategoryId != (int)InventoryCategory.Charge
                )
        )
        {
            var purchase = ammunitionSupply.AdvanceCareerShip(
                world,
                catalog,
                Pilot,
                training.Plan,
                Now,
                avoidedSuppliers: AvoidedSupplierSystems()
            );
            _operation.MarketChanged |= purchase.MarketChanged;
            if (purchase.Supplier is { } supplier)
            {
                BeginSupplyJourney(supplier);
                return;
            }
            if (purchase.Boarded)
            {
                _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
                ResetTradeHull();
                Wait("Ready to work in the purchased ship.");
                return;
            }
        }

        if (courier is not null)
        {
            if (!TryFreshDelivery(world, courier, startNew: true))
            {
                Wait("Waiting for a reachable distribution delivery.");
            }
            return;
        }

        FitRookieWeapon(world);
        _work = Work with { Journey = null };
        var supplies = resupply.Dock(database, world, catalog, pilotId, Work.Resupply, Now);
        _operation.MarketChanged |= supplies.SpentCents != (Work.Resupply?.SpentCents ?? 0);
        _work = Work with { Resupply = supplies };
        if (rookie ? !TryLaunchSortie(supplies, null) : !TryLaunchActivity(scoutedAnomaly))
        {
            Wait("Waiting for a viable bounty patrol.");
        }
    }

    private bool TryFreshDelivery(EconomyTransaction world, FreshCareerRoutes routes, bool startNew)
    {
        var catalog = _operation.Catalog;
        if (
            routes.Agent is not { } issuer
            || routes.Courier is not { } delivery
            || missions.RunStarterCourier(world, catalog, Pilot, issuer, delivery.DestinationStationId, Now, startNew) is not int station
        )
        {
            return false;
        }
        var route = CapsuleerTrafficRoutes.ToStation(
            catalog,
            Pilot.StationId,
            station,
            (HullClass)Pilot.ShipGroupId,
            AvoidedSupplierSystems(),
            station == issuer.StationId ? CapsuleerRecruitmentRules.MaximumJumps : FreshCareerRules.DeliveryJumps,
            policy: world.Settings.Capsuleers
        );
        if (route is null)
        {
            Wait("Waiting for a reachable distribution delivery.");
        }
        else
        {
            BeginSupplyJourney(route);
        }
        return true;
    }

    private void FitRookieWeapon(EconomyTransaction world)
    {
        if (Pilot.ShipGroupId != (int)HullClass.Corvette)
        {
            return;
        }
        int weapon = (int)CombatWeaponType.CivilianGatlingRailgun;
        var stock = world
            .Filter<OwnedItem>(item => item.OwnerId == pilotId)
            .Where(item => item.TypeId == weapon && item.Quantity > 0)
            .ToArray();
        if (
            stock.Any(item => item.LocationId == Pilot.ShipId && InventoryDefinitions.IsHighSlot(item.FlagId))
            || stock.FirstOrDefault(item => item.LocationId == Pilot.StationId && item.FlagId == (int)InventoryFlag.Hangar) is not { } owned
        )
        {
            return;
        }
        var catalog = _operation.Catalog;
        var fitted = PilotFittingStore
            .Fit(world, catalog, pilotId, [owned.Id], Pilot.StationId, (int)InventoryFlag.AutoFit, Now)
            .Single(change => change.Item.LocationId == Pilot.ShipId);
        PilotFittingStore.SetOnline(world, catalog, pilotId, Pilot.ShipId, fitted.Item.Id, true, Now);
        _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
    }
}
