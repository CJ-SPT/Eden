using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Ships;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool DynamicWorkPilot(EconomyTransaction world) =>
        world.Set<SavedTrafficIdentity>().Find(pilotId) is null && CapsuleerIndustry.Employer(world, pilotId, Pilot.CorpId) is not null;

    private bool TryDynamicDockWork(CombatAnomaly? scoutedAnomaly)
    {
        if (
            _operation.World is not EconomyTransaction world
            || !DynamicWorkPilot(world)
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

        var catalog = _operation.Catalog;
        long now = Now;
        bool industryDemand = CapsuleerIndustry.HasWorkDemand(world, catalog, Pilot, now, requireProductBlueprint: true);
        bool factoryJob = CapsuleerIndustry.HasStationFactoryJob(world, pilotId, Pilot.StationId);
        bool eligibleHuntSystem = database.Settings.Capsuleers.RoamPercent > 0 && PatrolAllowed(new(PatrolKind.Roam, Pilot.SystemId));
        bool canHunt = eligibleHuntSystem && !_background;
        int replacements = canHunt ? CapsuleerIndustry.ReadyHuntReplacements(world, catalog, Pilot) : 0;
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

        if (eligibleHuntSystem && work != CapsuleerWorkKind.Industry)
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
                }
                else if (TryGangDeparture(authorizedHunt: true) || TryLaunchSortie(supplies, scoutedAnomaly, fit, intentionalHunt: true))
                {
                    return true;
                }
            }
            else
            {
                _operation.MarketChanged |= ammunitionSupply.TryReplenishHuntReserve(world, catalog, Pilot, now, candidate);
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
}
