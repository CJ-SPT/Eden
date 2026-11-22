using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Combat;
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
            _work = Work with { SovereigntyExpedition = null, HomeDefenseAllianceId = null };
            if (DynamicWorkPilot(world) && ammunitionSupply.TryBoardOwnedReplacement(world, catalog, Pilot, CapsuleerCareer.Hauling, Now))
            {
                _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
                ResetTradeHull();
                return;
            }
            _pilot = fleet.RecoverCorvette(world, catalog, pilotId, Pilot.ShipId, Pilot.DockedLocationId).Pilot;
            ResetTradeHull();
            Wait("Boarded the recovery corvette.");
            return;
        }

        bool rookie = Pilot.ShipGroupId == (int)HullClass.Corvette;
        var profession =
            (rookie ? null : CapsuleerCareerTraining.Profession(catalog, Pilot.ShipTypeId, (HullClass)Pilot.ShipGroupId))
            ?? FreshCareerRules.Profession(agent.Slot);
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
            if (TryCorporationRelocation() || TrySovereigntyExpedition() || TryCorporationSupply())
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

        if (!rookie && TryDynamicDockWork(scoutedAnomaly))
        {
            return;
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
        var supplies = resupply.Dock(database, world, catalog, pilotId, Work.Resupply, Now, out var fit);
        _operation.MarketChanged |= supplies.SpentCents != (Work.Resupply?.SpentCents ?? 0);
        _work = Work with { Resupply = supplies };
        // The direct rookie launch still owns the fitted inputs read by resupply.
        if (rookie ? TryLaunchSortie(supplies, null, fit) : TryFrontierPatrol(world, supplies) || TryLaunchActivity(scoutedAnomaly))
        {
            return;
        }
        if (supplies.Requirements.Length > 0)
        {
            _work = Work with { Resupply = supplies with { Checked = false } };
        }
        Wait("Waiting for a viable bounty patrol.");
    }

    private bool TryFrontierPatrol(EconomyTransaction world, ResupplyProgress supplies)
    {
        if (
            !world.Settings.Capsuleers.BeltRatting
            || !world.Settings.NpcPopulation.Pirates
            || supplies.Requirements.Any(requirement => !requirement.Drone)
            || world.Set<PilotWallet>().Find(pilotId) is not { Cents: > 0 } wallet
        )
        {
            return false;
        }
        var catalog = _operation.Catalog;
        if (CapsuleerIndustry.CareerKit(catalog, Pilot.ShipTypeId) is not { } kit)
        {
            return false;
        }
        decimal reserve = kit.Parts().Sum(part => (decimal)CapsuleerIndustry.ReferencePrice(catalog, part.TypeId) * part.Quantity);
        if (reserve <= 0 || wallet.Cents < reserve)
        {
            return false;
        }
        var defense = CapsuleerDefenseProfile.Read(world, catalog, pilotId);
        var health = PilotHealthStore.Read(world, Pilot.ShipId, ShipHealth.DefenseFrom(defense.ShipAttributes), Now);
        if (CapsuleerDefenseRules.Retreat(health, defense.Combat) || !defense.RattingWeapons.Any(weapon => weapon.AvailableShots > 0))
        {
            return false;
        }

        bool canEnterNullSecurity =
            wallet.Cents >= reserve * FreshCareerRules.NullSecurityReserveHulls
            && CapsuleerIndustry.Employer(world, pilotId, Pilot.CorpId) is not null;
        var route = FreshCareerRoutes.FrontierPatrol(
            catalog,
            Pilot,
            world.Settings.Capsuleers,
            canEnterNullSecurity,
            AvoidedSupplierSystems()
        );
        if (route is null)
        {
            return false;
        }
        BeginSupplyJourney(route);
        return true;
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
