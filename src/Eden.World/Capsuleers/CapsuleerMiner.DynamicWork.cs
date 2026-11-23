using Eden.Data.World.Capsuleers;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerMiner
{
    private bool WaitForHuntReserve()
    {
        if (
            _operation.World is not EconomyTransaction world
            || industry.DeliverHuntReserveJob(world, _operation.Catalog, Pilot, Now) is not long deadline
        )
        {
            return false;
        }
        _work = Work with
        {
            Activity = MinerActivity.Waiting,
            DueTicks = deadline,
            DueStamp = CapsuleerScheduleRules.Stamp(deadline, Work.ClockOffsetTicks),
            WaitingReason = "Finishing production of a replacement part.",
        };
        return true;
    }

    private bool TryTakeHuntingWork()
    {
        var policy = database.Settings.Capsuleers;
        var catalog = _operation.Catalog;
        if (
            Work.FactoryStage is not null
            || Work.FactorySupply is not null
            || Work.RefineryIceType is not null
            || Work.Mercoxit is not null
            || _operation.World is not EconomyTransaction world
        )
        {
            return false;
        }

        bool earnedPvp = CapsuleerIndustry.RequiresEarnedPvp(world);
        bool canHunt = earnedPvp
            ? CapsuleerIndustry.CanStagePvp(world, catalog, Pilot.SystemId)
                && Live?.CapsuleerActivity.Read().ActiveSystems.Contains(Pilot.SystemId) == true
            : policy.RoamPercent > 0
                && PatrolRules.Allowed(
                    new(PatrolKind.Roam, Pilot.SystemId),
                    catalog.Definition(CapsuleerProvisioningCatalog.Security, Pilot.SystemId),
                    policy
                )
                && CapsuleerIndustry.Employer(world, pilotId, Pilot.CorpId) is not null;
        if (!canHunt)
        {
            return false;
        }

        long now = Now;
        if (
            CapsuleerIndustry.HasStationFactoryJob(world, pilotId, Pilot.StationId)
            || CapsuleerIndustry.HasWorkDemand(world, catalog, Pilot, now)
        )
        {
            return false;
        }
        if (earnedPvp)
        {
            var acquisition = industry.TryBuyEarnedPvpShip(world, catalog, database.Traffic, Pilot, now);
            _operation.MarketChanged |= acquisition.MarketChanged;
            if (!acquisition.Boarded)
            {
                return false;
            }
            var previous = Pilot;
            _pilot = ships.FindPilot(world, CapsuleerCommunityStore.MinerAccountId, pilotId)!;
            CapsuleerWorkHandoff.ToFlight(_operation, world, previous, world.Ships.Find(Pilot.ShipId)!, Work.ActiveTicks);
            _operation.TravelChanged = true;
            return true;
        }
        var combat = CapsuleerWorkHulls.Find(world, catalog, Pilot, CapsuleerProfession.Combat, now);
        if (combat is null)
        {
            return false;
        }
        if (CapsuleerIndustry.ReadyHuntReplacements(world, catalog, Pilot, combat) < CapsuleerWorkRules.MinimumHuntingReplacements)
        {
            _operation.MarketChanged |= industry.TryReplenishHuntReserve(world, catalog, Pilot, now, combat);
            return WaitForHuntReserve();
        }
        if (Live?.CapsuleerActivity.Read().ActiveSystems.Contains(Pilot.SystemId) != true)
        {
            return false;
        }
        CapsuleerWorkHulls.Board(world, catalog, Pilot, combat, now);
        CapsuleerWorkHandoff.ToFlight(_operation, world, Pilot, combat, Work.ActiveTicks);
        _pilot = ships.FindPilot(world, CapsuleerCommunityStore.MinerAccountId, pilotId)!;
        _operation.TravelChanged = true;
        return true;
    }
}
