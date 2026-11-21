using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool FreshCombat => database.Settings.FreshUniverse && Work.CareerPlan?.Profession == CapsuleerProfession.Combat;

    private void WorkFreshCareer(EconomyTransaction world)
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
        var training = careerTraining.AdvanceFreshCareer(world, catalog, Pilot, profession, Now);
        _work = Work with { CareerPlan = training.Plan };
        _operation.MarketChanged |= training.MarketChanged;
        if (training.LevelChanged)
        {
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
        }
        if (
            Pilot.ShipGroupId == (int)HullClass.Corvette
            && (
                profession != CapsuleerProfession.Hauling
                || !world.Filter<OwnedItem>(item => item.LocationId == Pilot.ShipId).Any(item => item.FlagId == (int)InventoryFlag.Cargo)
            )
        )
        {
            var purchase = ammunitionSupply.AdvanceCareerShip(world, catalog, Pilot, training.Plan!, Now);
            _operation.MarketChanged |= purchase.MarketChanged;
            if (purchase.Boarded)
            {
                _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
                ResetTradeHull();
                Wait("Ready to work in the purchased ship.");
                return;
            }
        }

        if (profession == CapsuleerProfession.Hauling)
        {
            var routes = FreshCareerRoutes.Read(catalog, agent.CommunitySystemId, world.Settings.Capsuleers);
            if (
                routes.Agent is { } issuer
                && routes.Courier is { } delivery
                && missions.RunStarterCourier(world, catalog, Pilot, issuer, delivery.DestinationStationId, Now) is int station
                && CapsuleerTrafficRoutes.ToStation(
                    catalog,
                    Pilot.StationId,
                    station,
                    (HullClass)Pilot.ShipGroupId,
                    AvoidedSupplierSystems(),
                    FreshCareerRules.DeliveryJumps,
                    policy: world.Settings.Capsuleers
                )
                    is { } route
            )
            {
                BeginSupplyJourney(route);
                return;
            }
            Wait("Waiting for a reachable distribution delivery.");
            return;
        }

        FitRookieWeapon(world);
        _work = Work with { Journey = null };
        var supplies = resupply.Dock(database, world, catalog, pilotId, Work.Resupply, Now);
        _operation.MarketChanged |= supplies.SpentCents != (Work.Resupply?.SpentCents ?? 0);
        _work = Work with { Resupply = supplies };
        if (!TryLaunchSortie(supplies, null))
        {
            Wait("Waiting for a viable bounty patrol.");
        }
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
