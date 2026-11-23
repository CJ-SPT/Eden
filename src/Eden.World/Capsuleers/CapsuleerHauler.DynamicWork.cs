using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool WaitForHuntReserve()
    {
        if (
            _operation.World is not EconomyTransaction world
            || ammunitionSupply.DeliverHuntReserveJob(world, _operation.Catalog, Pilot, Now) is not long deadline
        )
        {
            return false;
        }
        Wait("Finishing production of a replacement part.");
        _work = Work with { DueTicks = deadline, DueStamp = CapsuleerScheduleRules.Stamp(deadline, Work.ClockOffsetTicks) };
        return true;
    }

    private bool DynamicWorkPilot(EconomyTransaction world) =>
        world.Set<SavedTrafficIdentity>().Find(pilotId) is null && CapsuleerIndustry.Employer(world, pilotId, Pilot.CorpId) is not null;

    private bool HasPvpShip => CapsuleerIndustry.HasPvpShip(_operation.World, pilotId, Pilot.ShipId);
    private bool HasCommittedPvpShip =>
        !database.Settings.Capsuleers.EarnedPvpShips
        || CapsuleerCorporations.ReadOrganization(Live?.Economy.Peek<SavedCapsuleerOrganization>(pilotId)).EarnedPvpShipId == Pilot.ShipId;

    private bool TryDynamicDockWork(CombatAnomaly? scoutedAnomaly)
    {
        if (
            _operation.World is not EconomyTransaction world
            || Pilot.Position is not null
            || Pilot.InStructure
            || InActiveGang
            || Work.Freight is { Stacks.Length: > 0 }
            || Work.CorporationSupply is not null
            || Work.PlanetaryProduction is not null
            || Work.Resupply?.DestinationStationId is not null
            || Work.SovereigntyExpedition is not null
            || Work.HomeDefenseAllianceId is not null
            || Work.RegionalEscort is not null
            || Work.Exploration is not null
            || Work.Journey is { } journey && journey.DestinationStationId != Pilot.StationId
        )
        {
            return false;
        }

        using var phase = SessionPerformance.Phase("traffic.dynamicDockWork");
        var catalog = _operation.Catalog;
        long now = Now;
        bool earnedPvp = CapsuleerIndustry.RequiresEarnedPvp(world);
        bool eligibleHuntSystem = earnedPvp
            ? CapsuleerIndustry.CanStagePvp(world, catalog, Pilot.SystemId)
            : database.Settings.Capsuleers.RoamPercent > 0 && PatrolAllowed(new(PatrolKind.Roam, Pilot.SystemId));
        bool canHunt = eligibleHuntSystem && !_background;
        if (!(earnedPvp && canHunt) && !DynamicWorkPilot(world))
        {
            return false;
        }
        bool industryDemand = CapsuleerIndustry.HasWorkDemand(world, catalog, Pilot, now, requireProductBlueprint: true);
        bool factoryJob = CapsuleerIndustry.HasStationFactoryJob(world, pilotId, Pilot.StationId);
        int replacements = canHunt && !earnedPvp ? CapsuleerIndustry.ReadyHuntReplacements(world, catalog, Pilot) : 0;
        var work = CapsuleerWorkRules.Choose(industryDemand || factoryJob, canHunt, replacements);
        if (work == CapsuleerWorkKind.Industry)
        {
            _operation.MarketChanged |= ammunitionSupply.ProduceScheduled(world, catalog, Pilot, now, out var supply);
            _work = Work with { FactorySupply = supply };
            if (supply is not null || CapsuleerIndustry.HasStationFactoryJob(world, pilotId, Pilot.StationId))
            {
                Wait(supply is null ? "Finishing station production." : "Waiting for factory inputs.");
                return true;
            }
        }
        else if (Work.FactorySupply is not null)
        {
            _work = Work with { FactorySupply = null };
        }

        if (earnedPvp && canHunt && work != CapsuleerWorkKind.Industry)
        {
            if (TryEarnedPvpDeparture(world, scoutedAnomaly))
            {
                return true;
            }
        }
        else if (!earnedPvp && eligibleHuntSystem && work != CapsuleerWorkKind.Industry)
        {
            var candidate = CapsuleerWorkHulls.Find(
                world,
                catalog,
                Pilot,
                CapsuleerProfession.Combat,
                now,
                improveCurrent: work == CapsuleerWorkKind.Hunt
            );
            if (
                canHunt
                && candidate is not null
                && CapsuleerIndustry.ReadyHuntReplacements(world, catalog, Pilot, candidate)
                    >= CapsuleerWorkRules.MinimumHuntingReplacements
            )
            {
                CapsuleerWorkHulls.Board(world, catalog, Pilot, candidate, now);
                _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
                ResetTradeHull();
                work = CapsuleerWorkKind.Hunt;
            }
            if (work == CapsuleerWorkKind.Hunt)
            {
                UnloadRattingLoot();
                var supplies = resupply.Dock(database, world, catalog, pilotId, Work.Resupply, now, out var fit);
                _operation.MarketChanged |= supplies.SpentCents != (Work.Resupply?.SpentCents ?? 0);
                _work = Work with { Resupply = supplies };
                if (CapsuleerIndustry.ReadyHuntReplacements(world, catalog, Pilot) < CapsuleerWorkRules.MinimumHuntingReplacements)
                {
                    work = CapsuleerWorkKind.Routine;
                    _operation.MarketChanged |= ammunitionSupply.TryReplenishHuntReserve(world, catalog, Pilot, now);
                    if (WaitForHuntReserve())
                    {
                        return true;
                    }
                }
                else if (TryGangDeparture(authorizedHunt: true) || TryLaunchSortie(supplies, scoutedAnomaly, fit, intentionalHunt: true))
                {
                    return true;
                }
            }
            else
            {
                _operation.MarketChanged |= ammunitionSupply.TryReplenishHuntReserve(world, catalog, Pilot, now, candidate);
                if (WaitForHuntReserve())
                {
                    return true;
                }
            }
        }

        var profession = CapsuleerCareerTraining.Profession(catalog, Pilot.ShipTypeId, (HullClass)Pilot.ShipGroupId);
        if (
            (industryDemand || canHunt && work != CapsuleerWorkKind.Hunt || profession == CapsuleerProfession.Mining)
            && database.Settings.Capsuleers.Miners
            && CapsuleerWorkHulls.Find(world, catalog, Pilot, CapsuleerProfession.Mining, now) is { } miner
        )
        {
            CapsuleerWorkHulls.Board(world, catalog, Pilot, miner, now);
            CapsuleerWorkHandoff.ToMining(_operation, world, Pilot, miner, Work.ActiveTicks);
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
            _operation.TravelChanged = true;
            return true;
        }
        return false;
    }

    private bool TryEarnedPvpDeparture(EconomyTransaction world, CombatAnomaly? scoutedAnomaly)
    {
        UnloadRattingLoot();
        var acquisition = ammunitionSupply.TryBuyEarnedPvpShip(world, _operation.Catalog, database.Traffic, Pilot, Now);
        _operation.MarketChanged |= acquisition.MarketChanged;
        if (acquisition.Boarded)
        {
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
            ResetTradeHull();
        }
        else if (TryShopForPvpShip(world))
        {
            return true;
        }
        if (!HasPvpShip)
        {
            return false;
        }
        var supplies = resupply.Dock(database, world, _operation.Catalog, pilotId, Work.Resupply, Now, out var fit);
        _operation.MarketChanged |= supplies.SpentCents != (Work.Resupply?.SpentCents ?? 0);
        _work = Work with { Resupply = supplies };
        return CapsuleerCorporations.Organization(world, pilotId).PvpFitGoal is not null
                && TryLaunchSortie(supplies, scoutedAnomaly, fit, fundingFit: true)
            || TryGangDeparture(authorizedHunt: true)
            || TryLaunchSortie(supplies, scoutedAnomaly, fit, intentionalHunt: true);
    }
}
