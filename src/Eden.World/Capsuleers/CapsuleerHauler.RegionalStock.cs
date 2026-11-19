using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerRegionalStockRules;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private CapsuleerIndustry.Collection? PlanRegionalStock(EconomyTransaction world, LiveWorld live, IReadOnlySet<int> avoided)
    {
        if (Work.RegionalStockRetryAt > Now)
        {
            return null;
        }
        _work = Work with { RegionalStockRetryAt = checked(Now + Rules.ReviewInterval.Ticks) };
        return CapsuleerIndustry.PlanRegionalStock(world, _operation.Catalog, live, Pilot, avoided, Now);
    }

    private bool HandleRegionalStock(EconomyTransaction world, CorporationSupplyRun run, RegionalStockTrip trip)
    {
        var catalog = _operation.Catalog;
        if (Work.Freight is { } freight)
        {
            if (Pilot.StationId != trip.Headquarters)
            {
                return ContinueFactoryFreight(trip.Headquarters, Rules.MaximumJumps);
            }
            CapsuleerIndustry.Deliver(world, catalog, Pilot, run, freight);
            _work = Work with { RegionalStockRetryAt = checked(Now + Rules.ReviewInterval.Ticks) };
            return CompleteFactorySupply();
        }
        if (Pilot.StationId != trip.Supplier)
        {
            return ContinueFactoryFreight(trip.Supplier, Rules.MaximumJumps);
        }

        var employer = CapsuleerIndustry.Employer(world, pilotId, run.CorporationId);
        if (trip.OrderIds is null)
        {
            var orders = employer is null ? [] : ammunitionSupply.PlaceRegionalStockBids(world, catalog, Pilot, employer, trip);
            trip = trip with { OrderIds = orders, BidDeadline = checked(Now + Rules.BidWindow.Ticks) };
            run = run with { RegionalStock = trip };
            _work = Work with { CorporationSupply = run };
            _operation.MarketChanged |= orders.Length > 0;
        }

        var bids = CapsuleerIndustry.RegionalStockBids(world, Pilot, run, trip);
        if (employer is not null && Now < trip.BidDeadline && bids.Any(bid => bid.Status == PersonalOrderStatus.Open))
        {
            Wait("Waiting for regional buy orders to fill.");
            return true;
        }
        _operation.MarketChanged |= ammunitionSupply.CancelRegionalStockBids(world, catalog, bids);
        var cargo = CapsuleerIndustry.CollectRegionalStock(world, catalog, Pilot, trip.Headquarters, bids);
        if (cargo is null)
        {
            Wait("Waiting for room in the regional courier's cargo hold.");
            return true;
        }
        _work = Work with { Freight = cargo };
        return ContinueFactoryFreight(trip.Headquarters, Rules.MaximumJumps);
    }
}
