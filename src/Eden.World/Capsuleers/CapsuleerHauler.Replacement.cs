using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private void AttemptShipReplacement()
    {
        using var phase = SessionPerformance.Phase("traffic.shipReplacement");
        if (_operation.World is EconomyTransaction world)
        {
            long now = Now;
            if (TryResumeWorkAfterPvpLoss(world, now))
            {
                return;
            }
            var result = ammunitionSupply.ReplaceShip(
                world,
                _operation.Catalog,
                Pilot,
                CapsuleerCareer.Hauling,
                now,
                Work.ReplacementOrderDeadline,
                Work.ReplacementOrderIds
            );
            _operation.MarketChanged |= result.MarketChanged;
            _work = Work with { ReplacementOrderDeadline = result.OrderDeadline, ReplacementOrderIds = result.OrderIds };
            if (result.Boarded)
            {
                _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
                ResetTradeHull();
                return;
            }
            if (result.OrderDeadline <= now)
            {
                Wait("Waiting for an affordable replacement in stock.", CapsuleerReplacementRules.StockRetrySeconds);
                return;
            }
        }
        Wait("Waiting for a replacement ship.");
    }

    private bool TryResumeWorkAfterPvpLoss(EconomyTransaction world, long now)
    {
        if (!CapsuleerIndustry.RequiresEarnedPvp(world) || Work.ReplacementOrderDeadline is not null || Pilot.InStructure)
        {
            return false;
        }
        if (ParkedWorkShipAfterPvpLoss(world) is not { } owned || owned.StationId != Pilot.StationId)
        {
            return false;
        }
        var previous = Pilot;
        var catalog = _operation.Catalog;
        CapsuleerWorkHulls.Board(world, catalog, previous, owned, now);
        var organization = CapsuleerCorporations.Organization(world, pilotId);
        CapsuleerCorporations.SaveOrganization(world, pilotId, organization with { EarnedPvpShipId = null, PvpWorkShipId = null });
        _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
        ResetTradeHull();
        if (CapsuleerCareerTraining.Profession(catalog, Pilot.ShipTypeId, (HullClass)Pilot.ShipGroupId) == CapsuleerProfession.Mining)
        {
            CapsuleerWorkHandoff.ToMining(_operation, world, previous, owned, Work.ActiveTicks);
        }
        return true;
    }

    private CapsuleerTrafficRoute? PvpLossRefuge()
    {
        if (
            !CanWorkHere
            || !CapsuleerIndustry.RequiresEarnedPvp(_operation.World)
            || ParkedWorkShipAfterPvpLoss(_operation.World) is not { } owned
            || _operation.Catalog.FindStation(owned.StationId)?.SolarSystemId != Pilot.SystemId
        )
        {
            return null;
        }
        return new(owned.StationId, owned.StationId, [Pilot.SystemId]);
    }

    private Ship? ParkedWorkShipAfterPvpLoss(WorldAction world)
    {
        var organization = CapsuleerCorporations.Organization(world, pilotId);
        return
            organization is { EarnedPvpShipId: long lostShip, PvpWorkShipId: long workShip }
            && world.Ships.Find(lostShip) is null
            && world.Ships.Find(workShip) is { Position: null, StoredInId: null, StructureId: null } owned
            && owned.OwnerId == pilotId
            ? owned
            : null;
    }

    private void ResetTradeHull()
    {
        _navigation = new NavigationState();
        _route = CapsuleerTrafficFlight.Route(Founder, _operation.Catalog);
        _operation.TravelChanged = true;
        _work = Work with
        {
            ShipId = Pilot.ShipId,
            Navigation = _navigation.Capture(),
            Journey = null,
            Retreating = false,
            TargetId = 0,
            DueTicks = 0,
            DueStamp = 0,
            WaitingReason = null,
            Resupply = null,
        };
    }
}
