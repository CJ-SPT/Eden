using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
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
        int maximumJumps = CapsuleerIndustryRules.MaximumFactorySupplyJumps;
        var nearbyCourier = NearbySystems(catalog, courier.SystemId, maximumJumps);
        foreach (var (manufacturer, pending) in requests)
        {
            var request = pending!;
            int factorySystem = catalog.RequireStation(request.StationId).SolarSystemId;
            var nearby = NearbySystems(catalog, factorySystem, maximumJumps);
            nearby.IntersectWith(nearbyCourier);
            foreach (var need in request.Materials)
            {
                int[] suppliers = SupplierStations(world, nearby, request.StationId, need, now).ToArray();
                if (suppliers.Length == 0)
                {
                    continue;
                }
                int quantity = FactoryShortfall(world, catalog, manufacturer, request, need);
                int capacity = FreightUnits(catalog, need, free, quantity);
                if (capacity <= 0)
                {
                    continue;
                }
                Collection? best = null;
                int bestQuantity = 0;
                int bestJumps = int.MaxValue;
                long bestPrice = long.MaxValue;
                foreach (int supplier in suppliers)
                {
                    if (!catalog.Definitions.Stations.ContainsKey(supplier))
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
                    if (ShopForFactory(world, catalog, courier, employer, supplier, need, capacity) is not { } purchase)
                    {
                        continue;
                    }
                    int jumps = outbound.Systems.Length + inbound.Systems.Length;
                    if (
                        purchase.Quantity < bestQuantity
                        || purchase.Quantity == bestQuantity
                            && (jumps > bestJumps || jumps == bestJumps && purchase.LimitPriceCents >= bestPrice)
                    )
                    {
                        continue;
                    }
                    bestQuantity = purchase.Quantity;
                    bestJumps = jumps;
                    bestPrice = purchase.LimitPriceCents;
                    var shipment = new FactorySupplyShipment(
                        manufacturer,
                        request.StationId,
                        supplier,
                        need.TypeId,
                        request.Demand?.OrderId
                    );
                    // Factory trips quote the station's book on arrival, so they have no fixed supplier order ID.
                    best = new(new(employer.Id, 0) { FactorySupply = shipment }, outbound);
                }
                if (best is not null)
                {
                    return best;
                }
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
            || request.Materials.FirstOrDefault(material => material.TypeId == shipment.TypeId) is not { } need
        )
        {
            return null;
        }
        int quantity = FactoryShortfall(world, catalog, shipment.ManufacturerId, request, need);
        int capacity = FreightUnits(catalog, need, FreeFreightVolume(world, catalog, courier), quantity);
        if (capacity <= 0 || ShopForFactory(world, catalog, courier, employer, courier.StationId, need, capacity) is not { } purchase)
        {
            return null;
        }
        var result = market.BuyManufacturingInputs(world, catalog, courier.PilotId, courier.StationId, employer, [purchase]);
        int bought = result.Trades.Sum(trade => trade.Quantity);
        return StowPurchased(world, courier, shipment.FactoryStationId, need.TypeId, bought, result.Trades[0].OrderId);
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

    private int FactoryShortfall(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long manufacturer,
        FactorySupplyRequest request,
        FactorySupplyNeed need
    )
    {
        long held = world
            .Filter<OwnedItem>(item => item.OwnerId == manufacturer)
            .Where(item => item.LocationId == request.StationId && item.TypeId == need.TypeId && item.FlagId == (int)InventoryFlag.Hangar)
            .Sum(item => (long)item.Quantity);
        int target = (int)Math.Clamp(need.HangarTarget - held, 0, need.PlannedUnits);
        if (target == 0)
        {
            return 0;
        }
        var local = market
            .SupplyOrders(world, catalog, request.StationId, [need.TypeId], manufacturer, request.CorporationId)
            .Where(order => order.PriceCents <= need.MaximumUnitPriceCents);
        int outstanding = (int)Math.Max(0, need.RequiredQuantity - held);
        int localUnits = QuoteInput(local, need.TypeId, outstanding)?.Quantity ?? 0;
        return Math.Min(target, outstanding - localUnits);
    }

    private static int FreightUnits(CatalogDbContext catalog, FactorySupplyNeed need, double free, int quantity)
    {
        double volume = PilotInventoryStore.Volume(catalog, need.TypeId);
        return volume > 0 ? (int)Math.Clamp(Math.Floor(free / volume), 0, quantity) : 0;
    }

    private MarketPurchase? ShopForFactory(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        SavedCorporation employer,
        int station,
        FactorySupplyNeed need,
        int capacity
    )
    {
        var offers = market
            .SupplyOrders(world, catalog, station, [need.TypeId], courier.PilotId, employer.Id)
            .TakeWhile(offer => offer.PriceCents <= Math.Min(need.MaximumUnitPriceCents, employer.BalanceCents));
        var purchase = QuoteInput(offers, need.TypeId, capacity);
        if (purchase is null || purchase.Quantity <= employer.BalanceCents / purchase.LimitPriceCents)
        {
            return purchase;
        }
        int affordable = (int)(employer.BalanceCents / purchase.LimitPriceCents);
        return QuoteInput(offers.TakeWhile(offer => offer.PriceCents <= purchase.LimitPriceCents), need.TypeId, affordable);
    }

    private static IEnumerable<int> SupplierStations(
        EconomyTransaction world,
        IReadOnlySet<int> nearby,
        int factory,
        FactorySupplyNeed need,
        long now
    ) =>
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
            .AsEnumerable()
            .Where(order => nearby.Contains(order.SystemId))
            .Select(order => order.StationId)
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
                    .AsEnumerable()
                    .Where(order => nearby.Contains(order.SystemId))
                    .Select(order => order.StationId)
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
