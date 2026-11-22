using Eden.Data.World.Capsuleers;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
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

    private bool TryRookieFlight(EconomyTransaction world, bool localBounty = false)
    {
        var policy = world.Settings.Capsuleers;
        var catalog = _operation.Catalog;
        if (
            Pilot.ShipGroupId != (int)HullClass.Corvette
            || world.Set<SavedCapsuleerAgent>().Find(pilotId) is not { } agent
            || world.Filter<IndustryJob>(job => job.PilotId == pilotId).Any(job => job.Status == IndustryJobStatus.Installed)
        )
        {
            return false;
        }
        CapsuleerProfession? work = null;
        if (policy.FreshCourierPilots && FreshCareerRoutes.Read(catalog, agent.CommunitySystemId, policy).Courier is { } delivery)
        {
            int destination = Pilot.StationId == delivery.SourceStationId ? delivery.DestinationStationId : delivery.SourceStationId;
            if (
                CapsuleerTrafficRoutes.ToStation(
                    catalog,
                    Pilot.StationId,
                    destination,
                    HullClass.Corvette,
                    maximumJumps: CapsuleerRecruitmentRules.MaximumJumps,
                    policy: policy
                )
                is not null
            )
            {
                work = CapsuleerProfession.Hauling;
            }
        }
        if (
            work is null
            && localBounty
            && policy.FreshBountyPilots
            && policy.BeltRatting
            && world.Settings.NpcPopulation.Pirates
            && FreshCareerRoutes.Read(catalog, Pilot.SystemId, policy).Combat is not null
            && world
                .Filter<OwnedItem>(item => item.OwnerId == pilotId)
                .AsEnumerable()
                .Any(item =>
                    item.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun
                    && item.Quantity > 0
                    && (
                        item.LocationId == Pilot.ShipId && InventoryDefinitions.IsHighSlot(item.FlagId)
                        || item.LocationId == Pilot.StationId && item.FlagId == (int)InventoryFlag.Hangar
                    )
                )
        )
        {
            work = CapsuleerProfession.Combat;
        }
        if (work is null)
        {
            return false;
        }
        CapsuleerWorkHandoff.ToFlight(_operation, world, Pilot, world.Ships.Find(Pilot.ShipId)!, Work.ActiveTicks, work);
        return true;
    }

    private bool TryTakeHuntingWork()
    {
        var policy = database.Settings.Capsuleers;
        var catalog = _operation.Catalog;
        if (
            policy.RoamPercent <= 0
            || !PatrolRules.Allowed(
                new(PatrolKind.Roam, Pilot.SystemId),
                catalog.Definition(CapsuleerProvisioningCatalog.Security, Pilot.SystemId),
                policy
            )
            || Work.FactoryStage is not null
            || Work.FactorySupply is not null
            || Work.RefineryIceType is not null
            || Work.Mercoxit is not null
            || _operation.World is not EconomyTransaction world
            || CapsuleerIndustry.Employer(world, pilotId, Pilot.CorpId) is null
        )
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
