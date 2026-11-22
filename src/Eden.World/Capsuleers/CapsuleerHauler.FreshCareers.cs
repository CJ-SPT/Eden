using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
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
        var policy = world.Settings.Capsuleers;
        var preference =
            (rookie ? null : CapsuleerCareerTraining.Profession(catalog, Pilot.ShipTypeId, (HullClass)Pilot.ShipGroupId))
            ?? Work.CareerPlan?.Profession
            ?? FreshCareerRules.Profession(agent.Slot);
        bool preferCourier = preference == CapsuleerProfession.Hauling;
        bool reconsider = !rookie || ReviewRookieWork();
        FreshCareerRoutes? routes = null;
        FreshCareerRoutes Routes() => routes ??= FreshCareerRoutes.Read(catalog, agent.CommunitySystemId, policy);
        if ((Work.Journey is not null || preferCourier) && TryFreshDelivery(world, Routes(), startNew: false))
        {
            return;
        }
        bool bounty =
            (!preferCourier || reconsider)
            && policy.FreshBountyPilots
            && policy.BeltRatting
            && world.Settings.NpcPopulation.Pirates
            && catalog.BeltAnchors(Pilot.SystemId).Count > 0;
        bool courier = policy.FreshCourierPilots && (preferCourier || reconsider && !bounty) && Routes().Courier is not null;
        if (rookie && reconsider && TryRookieMining(world, requireDemand: true))
        {
            return;
        }
        var profession =
            courier && (!bounty || preference == CapsuleerProfession.Hauling) ? CapsuleerProfession.Hauling
            : bounty ? CapsuleerProfession.Combat
            : preference;
        if (!rookie)
        {
            JoinCorporationAtDock();
            if (TryCorporationRelocation() || TrySovereigntyExpedition() || TryCorporationSupply())
            {
                return;
            }
        }

        var training = rookie
            ? careerTraining.AdvanceFreshCareer(world, catalog, Pilot, profession, Now)
            : careerTraining.Advance(world, catalog, Pilot, preference, Work.CareerPlan, Now);
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
        if (profession == CapsuleerProfession.Hauling && courier && TryFreshDelivery(world, Routes(), startNew: true))
        {
            return;
        }

        if (bounty && (rookie || profession == CapsuleerProfession.Combat) && TryFreshBounty(world, scoutedAnomaly, rookie, training.Plan))
        {
            return;
        }
        if (
            profession != CapsuleerProfession.Hauling
            && reconsider
            && policy.FreshCourierPilots
            && TryFreshDelivery(world, Routes(), startNew: true)
        )
        {
            return;
        }
        if (!rookie || !reconsider || !TryRookieMining(world, requireDemand: false))
        {
            int delay = rookie && _background ? FreshCareerRules.BackgroundIdleRetrySeconds(pilotId) : HaulingRules.PollSeconds;
            Wait("Waiting for reachable starter work.", delay);
        }
    }

    private bool ReviewRookieWork()
    {
        if (!_background)
        {
            return true;
        }
        if (Work.RookieWorkReviewAt > Work.ActiveTicks)
        {
            return false;
        }
        bool alreadyStaggered = Work.RookieWorkReviewAt != 0;
        _work = Work with
        {
            RookieWorkReviewAt = FreshCareerRules.NextBackgroundReview(pilotId, Work.ActiveTicks, opening: !alreadyStaggered),
        };
        return alreadyStaggered;
    }

    private bool TryRookieMining(EconomyTransaction world, bool requireDemand)
    {
        var catalog = _operation.Catalog;
        if (
            !world.Settings.Capsuleers.Miners
            || CapsuleerProvisioningCatalog.StarterIndustry(catalog, Pilot.SystemId).HomeStationId != Pilot.StationId
            || requireDemand && !CapsuleerIndustry.HasStarterWorkDemand(world, catalog, Pilot, Now)
            || Work.Freight is { Stacks.Length: > 0 }
            || Work.CorporationSupply is not null
            || Work.SovereigntyExpedition is not null
            || Work.Resupply?.DestinationStationId is not null
            || !OwnsRookieEquipment(world, (int)MiningLaserType.Civilian)
            || world
                .Filter<OwnedItem>(item => item.LocationId == Pilot.ShipId)
                .AsEnumerable()
                .Any(item =>
                    ShipHoldRules.IsHold(item.FlagId) && catalog.FittingType(item.TypeId)?.CategoryId != (int)InventoryCategory.Charge
                )
        )
        {
            return false;
        }
        var previous =
            Live?.MinerCheckpoints.GetValueOrDefault(pilotId)?.Work
            ?? MinerCheckpoint.Read(world.Filter<SavedCapsuleerCommunity>(row => row.PilotId == pilotId).SingleOrDefault()?.Progress);
        if (previous is { Activity: MinerActivity.Waiting } && previous.DueTicks > Work.ActiveTicks)
        {
            return false;
        }
        CapsuleerWorkHandoff.ToMining(_operation, world, Pilot, world.Ships.Find(Pilot.ShipId)!, Work.ActiveTicks);
        return true;
    }

    private bool TryFreshBounty(EconomyTransaction world, CombatAnomaly? scoutedAnomaly, bool rookie, CapsuleerCareerPlan? training)
    {
        if (rookie && !TryFitRookieWeapon(world))
        {
            return false;
        }
        var prior = Work.CareerPlan;
        if (rookie && prior?.Profession != CapsuleerProfession.Combat)
        {
            _work = Work with { CareerPlan = new(CapsuleerProfession.Combat, 0, CapsuleerTrainingFocus.Work) };
        }
        _work = Work with { Journey = null };
        var supplies = resupply.Dock(database, world, _operation.Catalog, pilotId, Work.Resupply, Now, out var fit);
        _operation.MarketChanged |= supplies.SpentCents != (Work.Resupply?.SpentCents ?? 0);
        _work = Work with { Resupply = supplies };
        if (rookie ? TryLaunchSortie(supplies, null, fit) : TryFrontierPatrol(world, supplies) || TryLaunchActivity(scoutedAnomaly))
        {
            return true;
        }
        _work = Work with
        {
            CareerPlan = training,
            Resupply = supplies.Requirements.Length > 0 ? supplies with { Checked = false } : supplies,
        };
        return false;
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
        if (routes.Agent is not { } issuer || routes.Courier is not { } delivery || issuer.StationId is not int source)
        {
            return false;
        }
        int proposed = Pilot.StationId == source ? delivery.DestinationStationId : source;
        var policy = world.Settings.Capsuleers;
        var avoided = AvoidedSupplierSystems();
        var prepared = startNew ? Route(proposed) : null;
        if (startNew && prepared is null)
        {
            return false;
        }
        if (missions.RunStarterCourier(world, catalog, Pilot, issuer, delivery.DestinationStationId, Now, startNew) is not int station)
        {
            return false;
        }
        var route = station == proposed ? prepared ?? Route(station) : Route(station);
        if (route is null)
        {
            Wait("Waiting for a reachable distribution delivery.");
        }
        else
        {
            BeginSupplyJourney(route);
        }
        return true;

        CapsuleerTrafficRoute? Route(int destination)
        {
            var preparedRoute = destination == source ? routes.Return : delivery;
            if (
                preparedRoute is not null
                && preparedRoute.SourceStationId == Pilot.StationId
                && preparedRoute.DestinationStationId == destination
                && TrafficRules.RouteAllows((HullClass)Pilot.ShipGroupId, visitsHighSecurity: true)
                && policy.CanUse(catalog.Definitions, preparedRoute)
                && !preparedRoute.Systems.Skip(1).Any(avoided.Contains)
            )
            {
                return preparedRoute;
            }
            return CapsuleerTrafficRoutes.ToStation(
                catalog,
                Pilot.StationId,
                destination,
                (HullClass)Pilot.ShipGroupId,
                avoided,
                destination == source ? CapsuleerRecruitmentRules.MaximumJumps : FreshCareerRules.DeliveryJumps,
                policy: policy
            );
        }
    }

    private bool TryFitRookieWeapon(EconomyTransaction world)
    {
        int weapon = (int)CombatWeaponType.CivilianGatlingRailgun;
        var catalog = _operation.Catalog;
        var stock = world
            .Filter<OwnedItem>(item => item.OwnerId == pilotId)
            .Where(item => item.TypeId == weapon && item.Quantity > 0)
            .ToArray();
        var fitted = stock.FirstOrDefault(item => item.LocationId == Pilot.ShipId && InventoryDefinitions.IsHighSlot(item.FlagId));
        if (fitted is { Online: true })
        {
            return true;
        }
        long equipped;
        if (fitted is not null)
        {
            equipped = fitted.Id;
        }
        else
        {
            var owned = stock.FirstOrDefault(item => item.LocationId == Pilot.StationId && item.FlagId == (int)InventoryFlag.Hangar);
            if (owned is null)
            {
                return false;
            }
            equipped = PilotFittingStore
                .Fit(world, catalog, pilotId, [owned.Id], Pilot.StationId, (int)InventoryFlag.AutoFit, Now)
                .Single(change => change.Item.LocationId == Pilot.ShipId)
                .Item.Id;
        }
        PilotFittingStore.SetOnline(world, catalog, pilotId, Pilot.ShipId, equipped, true, Now);
        _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
        return true;
    }

    private bool OwnsRookieEquipment(EconomyTransaction world, int type) =>
        world
            .Filter<OwnedItem>(item => item.OwnerId == pilotId)
            .Any(item =>
                item.TypeId == type
                && item.Quantity > 0
                && (
                    item.LocationId == Pilot.ShipId
                        && item.FlagId >= (int)InventoryFlag.HighSlot0
                        && item.FlagId <= (int)InventoryFlag.HighSlot7
                    || item.LocationId == Pilot.StationId && item.FlagId == (int)InventoryFlag.Hangar
                )
            );
}
