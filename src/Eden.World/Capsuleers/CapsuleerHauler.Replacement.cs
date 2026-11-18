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
            bool boarded = ammunitionSupply.ReplaceMerlin(world, _operation.Catalog, Pilot, Now, out bool marketChanged);
            _operation.MarketChanged |= marketChanged;
            if (boarded)
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
        }
        Wait("Waiting for a replacement ship.");
    }
}
