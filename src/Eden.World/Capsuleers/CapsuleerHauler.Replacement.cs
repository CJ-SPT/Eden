using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private void AttemptShipReplacement()
    {
        if (_operation.World is EconomyTransaction world)
        {
            long now = Now;
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
                _navigation = new NavigationState();
                _route = CapsuleerTrafficFlight.Route(Founder, _operation.Catalog);
                _operation.TravelChanged = true;
                _work = Work with
                {
                    ShipId = Pilot.ShipId,
                    Journey = null,
                    Retreating = false,
                    TargetId = 0,
                    DueTicks = 0,
                    DueStamp = 0,
                    WaitingReason = null,
                    Resupply = null,
                };
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
}
