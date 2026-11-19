using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private sealed record FactoryShortage(FactorySupplyNeed Need, int Quantity, double UnitVolume);

    private sealed record FactoryPickup(CapsuleerTrafficRoute Outbound, int RouteLength, IReadOnlyList<MarketPurchase> Purchases);

    internal Collection? PlanFactoryCollection(
        EconomyTransaction world,
        CatalogDbContext catalog,
        LiveWorld live,
        DockedPilot courier,
        IReadOnlySet<int> avoided,
        long now
    )
    {
        if (
            courier.ShipGroupId != (int)HullClass.Hauler
            || Employer(world, courier.PilotId, courier.CorpId) is not { BalanceCents: > 0 } employer
        )
        {
            return null;
        }
        var members = world.Filter<Pilot>(pilot => pilot.CorporationId == employer.Id).OrderBy(pilot => pilot.Id).ToArray();
        if (members.Any(pilot => live.HaulerCheckpoints.GetValueOrDefault(pilot.Id)?.Work.CorporationSupply is not null))
        {
            return null;
        }
        var requests = members
            .Select(member => (member.Id, Request: CurrentFactoryRequest(world, live, member.Id, employer.Id, now)))
            .Where(entry => entry.Request is not null)
            .ToArray();
        if (requests.Length == 0)
        {
            return null;
        }
        double free = FreeFreightVolume(world, catalog, courier);
        if (free <= 0)
        {
            return null;
        }
        foreach (var (manufacturer, pending) in requests)
        {
            var request = pending!;
            if (
                request.ResearchDatacores
                && PlanResearchCollection(world, catalog, courier, employer, manufacturer, request, avoided, now) is { } research
            )
            {
                return research;
            }
            int maximumJumps = request.MaximumJumps;
            int factorySystem = catalog.RequireStation(request.StationId).SolarSystemId;
            var suppliers = request
                .Materials.SelectMany(need =>
                    SupplierStations(world, request.StationId, need, now).Select(station => (station, need.TypeId))
                )
                .ToLookup(offer => offer.station, offer => offer.TypeId);
            if (suppliers.Count == 0)
            {
                continue;
            }
            var nearby = NearbySystems(catalog, factorySystem, maximumJumps);
            nearby.IntersectWith(NearbySystems(catalog, courier.SystemId, maximumJumps));
            var shortages = FactoryShortages(world, catalog, manufacturer, request);
            List<FactoryPickup> pickups = [];
            foreach (var offers in suppliers)
            {
                int supplier = offers.Key;
                var available = shortages.Where(shortage => offers.Contains(shortage.Need.TypeId)).ToArray();
                if (
                    available.Length == 0
                    || !catalog.Definitions.Stations.TryGetValue(supplier, out var station)
                    || !nearby.Contains(station.SolarSystemId)
                )
                {
                    continue;
                }
                var outbound = CapsuleerTrafficRoutes.ToStation(
                    catalog,
                    courier.StationId,
                    supplier,
                    HullClass.Hauler,
                    avoided,
                    maximumJumps
                );
                var inbound = CapsuleerTrafficRoutes.ToStation(
                    catalog,
                    supplier,
                    request.StationId,
                    HullClass.Hauler,
                    avoided,
                    maximumJumps
                );
                if (outbound is null || inbound is null)
                {
                    continue;
                }
                var purchases = ShopForFactory(world, catalog, courier.PilotId, employer, supplier, available, free);
                if (purchases.Count > 0)
                {
                    pickups.Add(new(outbound, outbound.Systems.Length + inbound.Systems.Length, purchases));
                }
            }
            var pickup = pickups
                .OrderByDescending(candidate => candidate.Purchases.Count)
                .ThenByDescending(candidate => candidate.Purchases.Sum(purchase => (long)purchase.Quantity))
                .ThenBy(candidate => candidate.RouteLength)
                .ThenBy(candidate => candidate.Purchases.Sum(purchase => purchase.Quantity * purchase.LimitPriceCents))
                .ThenBy(candidate => candidate.Outbound.DestinationStationId)
                .FirstOrDefault();
            if (pickup is not null)
            {
                var shipment = new FactorySupplyShipment(
                    manufacturer,
                    request.StationId,
                    pickup.Outbound.DestinationStationId,
                    request.Demand?.OrderId
                )
                {
                    Mercoxit = request.Mercoxit,
                };
                // Factory trips quote the station's book on arrival, so they have no fixed supplier order ID.
                return new(new(employer.Id, 0) { FactorySupply = shipment }, pickup.Outbound);
            }
        }
        return null;
    }

    internal FreightManifest? PurchaseFactoryMaterials(
        EconomyTransaction world,
        CatalogDbContext catalog,
        LiveWorld live,
        DockedPilot courier,
        CorporationSupplyRun run,
        FactorySupplyShipment shipment,
        long now
    )
    {
        if (
            courier.StationId != shipment.SupplierStationId
            || Employer(world, courier.PilotId, run.CorporationId) is not { BalanceCents: > 0 } employer
            || CurrentFactoryRequest(world, live, shipment.ManufacturerId, employer.Id, now) is not { } request
            || request.StationId != shipment.FactoryStationId
            || request.Demand?.OrderId != shipment.DemandOrderId
        )
        {
            return null;
        }
        var shortages = FactoryShortages(world, catalog, shipment.ManufacturerId, request);
        if (shipment.Research is { } research)
        {
            return request.ResearchDatacores ? ResearchPickup(world, catalog, courier, employer, shipment, research, shortages, now) : null;
        }
        var purchases = ShopForFactory(
            world,
            catalog,
            courier.PilotId,
            employer,
            courier.StationId,
            shortages,
            FreeFreightVolume(world, catalog, courier)
        );
        if (request.Mercoxit)
        {
            var ore = purchases.FirstOrDefault(purchase => s_rawMercoxit.Contains(purchase.TypeId));
            purchases = purchases.Where(purchase => !s_rawMercoxit.Contains(purchase.TypeId) || purchase == ore).ToArray();
        }
        if (purchases.Count == 0)
        {
            return null;
        }
        var result = market.BuyCorporationStock(world, catalog, courier.PilotId, courier.StationId, employer, purchases);
        var cargo = result
            .Trades.GroupBy(trade => trade.TypeId)
            .Select(trades => StowPurchased(world, courier, trades.Key, trades.Sum(trade => trade.Quantity)))
            .ToArray();
        return new(result.Trades[0].OrderId, shipment.FactoryStationId, cargo);
    }

    private static FactorySupplyRequest? CurrentFactoryRequest(
        EconomyTransaction world,
        LiveWorld live,
        long manufacturer,
        int corporation,
        long now
    )
    {
        if (world.Set<SavedCapsuleerAgent>().Find(manufacturer) is not { Career: CapsuleerCareer.Mining })
        {
            return null;
        }
        var work =
            live.MinerCheckpoints.GetValueOrDefault(manufacturer)?.Work
            ?? MinerCheckpoint.Read(world.Filter<SavedCapsuleerCommunity>(row => row.PilotId == manufacturer).SingleOrDefault()?.Progress);
        if (
            work?.FactorySupply is not { } request
            || request.CorporationId != corporation
            || world.Pilots.Find(manufacturer)?.ActiveShipId != work.ShipId
            || Employer(world, manufacturer, corporation) is null
        )
        {
            return null;
        }
        if (request.Demand is { } demand)
        {
            return
                world.Set<SavedPersonalOrder>().Find(demand.OrderId) is { Status: PersonalOrderStatus.Open, Bid: true } order
                && order.StationId == request.StationId
                && order.Expires > now
                && order.Remaining >= demand.Quantity
                && order.PriceCents >= demand.MinimumUnitPriceCents
                ? request
                : null;
        }
        return request;
    }

    private FactoryShortage[] FactoryShortages(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long manufacturer,
        FactorySupplyRequest request
    )
    {
        var inventory = Hangar(world, manufacturer, request.StationId)
            .GroupBy(item => item.TypeId)
            .ToDictionary(items => items.Key, items => items.Sum(item => (long)item.Quantity));
        var shortages = request
            .Materials.Select(need =>
                (Need: need, Quantity: (int)Math.Clamp(need.HangarTarget - inventory.GetValueOrDefault(need.TypeId), 0, need.PlannedUnits))
            )
            .Where(shortage => shortage.Quantity > 0)
            .ToArray();
        if (shortages.Length == 0)
        {
            return [];
        }
        int[] types = shortages.Select(shortage => shortage.Need.TypeId).ToArray();
        var local = market
            .SupplyOrders(world, catalog, request.StationId, types, manufacturer, request.CorporationId)
            .ToLookup(order => order.TypeId);
        List<FactoryShortage> remaining = [];
        foreach (var shortage in shortages)
        {
            var need = shortage.Need;
            int outstanding = (int)Math.Max(0, need.RequiredQuantity - inventory.GetValueOrDefault(need.TypeId));
            var offers = local[need.TypeId].TakeWhile(order => order.PriceCents <= need.MaximumUnitPriceCents);
            int localUnits = QuoteInput(offers, need.TypeId, outstanding)?.Quantity ?? 0;
            int quantity = Math.Min(shortage.Quantity, outstanding - localUnits);
            if (quantity > 0)
            {
                double volume = PilotInventoryStore.Volume(catalog, need.TypeId);
                if (volume > 0)
                {
                    remaining.Add(new(need, quantity, volume));
                }
            }
        }
        return remaining.ToArray();
    }

    private IReadOnlyList<MarketPurchase> ShopForFactory(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long courierId,
        SavedCorporation employer,
        int station,
        IReadOnlyList<FactoryShortage> shortages,
        double free
    )
    {
        if (shortages.Count == 0 || free <= 0)
        {
            return [];
        }
        int[] types = shortages.Select(shortage => shortage.Need.TypeId).ToArray();
        var offers = market.SupplyOrders(world, catalog, station, types, courierId, employer.Id).ToLookup(offer => offer.TypeId);
        List<MarketPurchase> purchases = [];
        long funds = employer.BalanceCents;
        // Smaller loads go first so a large shortage leaves room for other materials.
        foreach (var shortage in shortages.OrderBy(line => line.Quantity * line.UnitVolume).ThenBy(line => line.Need.TypeId))
        {
            double units = (free + InventoryDefinitions.ResourceTolerance) / shortage.UnitVolume;
            int capacity = (int)Math.Clamp(Math.Floor(units), 0, shortage.Quantity);
            if (capacity == 0 || funds <= 0)
            {
                continue;
            }
            var need = shortage.Need;
            if (QuoteFactoryInput(offers[need.TypeId], need, capacity, funds) is { } purchase)
            {
                purchases.Add(purchase);
                free -= purchase.Quantity * shortage.UnitVolume;
                funds -= purchase.Quantity * purchase.LimitPriceCents;
            }
        }
        return purchases;
    }

    private static MarketPurchase? QuoteFactoryInput(
        IEnumerable<MarketOrder> marketOffers,
        FactorySupplyNeed need,
        int capacity,
        long funds
    )
    {
        var offers = marketOffers.TakeWhile(offer => offer.PriceCents <= Math.Min(need.MaximumUnitPriceCents, funds));
        var purchase = QuoteInput(offers, need.TypeId, capacity);
        if (purchase is null || purchase.Quantity <= funds / purchase.LimitPriceCents)
        {
            return purchase;
        }
        int affordable = (int)(funds / purchase.LimitPriceCents);
        return QuoteInput(offers.TakeWhile(offer => offer.PriceCents <= purchase.LimitPriceCents), need.TypeId, affordable);
    }

    private static IEnumerable<int> SupplierStations(EconomyTransaction world, int factory, FactorySupplyNeed need, long now) =>
        world
            .Filter<SavedPersonalOrder>(order => order.TypeId == need.TypeId)
            .Where(order =>
                !order.Bid
                && order.Status == PersonalOrderStatus.Open
                && order.Remaining > 0
                && order.StationId != factory
                && order.Expires > now
                && order.PriceCents <= need.MaximumUnitPriceCents
            )
            .Select(order => order.StationId)
            .AsEnumerable()
            .Concat(
                world
                    .Filter<NpcMarketOrder>(order => order.TypeId == need.TypeId)
                    .Where(order =>
                        !order.Bid
                        && order.Remaining > 0
                        && order.StationId != factory
                        && order.Issued + (long)order.DurationDays * TimeSpan.TicksPerDay > now
                        && order.PriceCents <= need.MaximumUnitPriceCents
                    )
                    .Select(order => order.StationId)
                    .AsEnumerable()
            )
            .Distinct()
            .Order();

    private static HashSet<int> NearbySystems(CatalogDbContext catalog, int source, int jumps)
    {
        HashSet<int> nearby = [source];
        int[] frontier = [source];
        for (int jump = 0; jump < jumps && frontier.Length > 0; jump++)
        {
            frontier = frontier.SelectMany(system => CapsuleerTrafficRoutes.Neighbours(catalog, system)).Where(nearby.Add).ToArray();
        }
        return nearby;
    }
}
