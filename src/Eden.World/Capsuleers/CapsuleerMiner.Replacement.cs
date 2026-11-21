using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
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
                Work.ReplacementOrderIds
            );
            _operation.MarketChanged |= result.MarketChanged;
            _work = Work with { ReplacementOrderDeadline = result.OrderDeadline, ReplacementOrderIds = result.OrderIds };
            if (result.Boarded)
            {
                _pilot = ships.FindPilot(world, CapsuleerCommunityStore.MinerAccountId, pilotId)!;
                ResetMiningHull();
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
