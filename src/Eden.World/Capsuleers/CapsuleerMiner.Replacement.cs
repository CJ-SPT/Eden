using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerMiner
{
    private void AttemptShipReplacement()
    {
        int seconds = RetrySeconds;
        string waiting = "Waiting for a replacement mining ship.";
        if (_operation.World is EconomyTransaction world)
        {
            long now = Now;
            var result = industry.ReplaceShip(
                world,
                _operation.Catalog,
                Pilot,
                CapsuleerCareer.Mining,
                now,
                Work.ReplacementOrderDeadline,
                Work.ReplacementOrderIds,
                hubShopping: Work.ReplacementJourney?.DestinationStationId == Pilot.StationId
            );
            _operation.MarketChanged |= result.MarketChanged;
            _work = Work with { ReplacementOrderDeadline = result.OrderDeadline, ReplacementOrderIds = result.OrderIds };
            if (result.Boarded)
            {
                _pilot = ships.FindPilot(world, CapsuleerCommunityStore.MinerAccountId, pilotId)!;
                ResetMiningHull();
                ReturnFromReplacementHub();
                return;
            }
            if (!(result.OrderDeadline > now) && TryTravelToReplacementHub(world))
            {
                return;
            }
            if (result.OrderDeadline <= now)
            {
                seconds = CapsuleerReplacementRules.StockRetrySeconds;
                waiting = "Waiting for an affordable mining replacement in stock.";
            }
        }
        _work = Work with
        {
            Activity = MinerActivity.Waiting,
            DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(seconds).Ticks,
            DueStamp = Work.Stamp + seconds,
            WaitingReason = waiting,
        };
    }

    private bool TryTravelToReplacementHub(EconomyTransaction world)
    {
        if (industry.FindReplacementHub(world, _operation.Catalog, Pilot, CapsuleerCareer.Mining) is not { } route)
        {
            return false;
        }
        int home = Work.ReplacementJourney?.HomeStationId ?? Pilot.StationId;
        _work = Work with { ReplacementOrderDeadline = null, ReplacementOrderIds = null };
        StartReplacementJourney(
            new(TrafficJourneyKind.ReplacementShopping, home, route.SourceStationId, route.DestinationStationId, route.Systems, 0)
        );
        return true;
    }

    private void ReturnFromReplacementHub()
    {
        if (Work.ReplacementJourney is not { } trip)
        {
            return;
        }
        _work = Work with { ReplacementJourney = null };
        if (
            Pilot.StationId != trip.HomeStationId
            && CapsuleerTrafficRoutes.ToStation(
                _operation.Catalog,
                Pilot.StationId,
                trip.HomeStationId,
                (HullClass)Pilot.ShipGroupId,
                maximumJumps: int.MaxValue,
                policy: database.Settings.Capsuleers
            )
                is { } route
        )
        {
            StartReplacementJourney(
                trip with
                {
                    SourceStationId = route.SourceStationId,
                    DestinationStationId = route.DestinationStationId,
                    Systems = route.Systems,
                }
            );
        }
    }

    private void StartReplacementJourney(TrafficJourney journey)
    {
        _work = Work with
        {
            ReplacementJourney = journey,
            WaitingReason = null,
            WorkingBelt = 0,
            LeaveAfterCycle = false,
            Mercoxit = null,
            FactoryStage = null,
        };
        UndockMiner();
        RefreshObjects();
        BeginStationLeg(journey.Systems, journey.DestinationStationId);
    }

    private void ResetMiningHull()
    {
        _navigation = new NavigationState();
        _operation.TravelChanged = true;
        _work = Work with
        {
            ShipId = Pilot.ShipId,
            TargetId = 0,
            Navigation = _navigation.Capture(),
            DueTicks = 0,
            DueStamp = 0,
            WaitingReason = null,
            PaidUntil = 0,
            ModuleId = 0,
        };
    }
}
