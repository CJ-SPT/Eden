using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerRegionalStockRules;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool CanLaunchOffensiveFleet =>
        _operation.World is not EconomyTransaction world
        || CapsuleerIndustry.CanLaunchOffensiveFleet(world, _operation.Catalog, Pilot, Now);

    private CapsuleerIndustry.Collection? PlanRegionalStock(EconomyTransaction world, LiveWorld live, IReadOnlySet<int> avoided)
    {
        if (Work.RegionalStockRetryAt > Now)
        {
            return null;
        }
        int emptySupplier = Work.RegionalStockEmptySupplier;
        _work = Work with { RegionalStockRetryAt = checked(Now + Rules.ReviewInterval.Ticks), RegionalStockEmptySupplier = 0 };
        return CapsuleerIndustry.PlanRegionalStock(world, _operation.Catalog, live, Pilot, avoided, Now, emptySupplier);
    }

    private bool HandleRegionalStock(EconomyTransaction world, CorporationSupplyRun run, RegionalStockTrip trip)
    {
        var catalog = _operation.Catalog;
        int destination = trip.DeliveryStation ?? trip.Headquarters;
        if (Work.Freight is { } freight)
        {
            if (Pilot.StationId != destination)
            {
                return ContinueFactoryFreight(destination, Rules.MaximumJumps);
            }
            bool intact = EscortedFreightIntact(freight);
            CapsuleerIndustry.Deliver(world, catalog, Pilot, run, freight);
            DeliverRegionalShipment(intact);
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
        var cargo = CapsuleerIndustry.CollectRegionalStock(world, catalog, Pilot, destination, bids);
        if (cargo is null)
        {
            Wait("Waiting for room in the regional courier's cargo hold.");
            return true;
        }
        _work = Work with
        {
            Freight = cargo,
            RegionalStockEmptySupplier = cargo.Stacks.Length == 0 || bids.Any(bid => bid.Remaining > 0) ? trip.Supplier : 0,
        };
        return ContinueFactoryFreight(destination, Rules.MaximumJumps);
    }
}
