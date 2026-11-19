using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Ammo = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;
using Replacement = Eden.Gameplay.Capsuleers.CapsuleerReplacementRules;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerRegionalStockRules;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal static Collection? PlanRegionalStock(
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
            || Employer(world, courier.PilotId, courier.CorpId) is not { BalanceCents: > 0 } buyer
            || courier.StationId != buyer.StationId
            || !PilotMarketStore.HasSupplyListingSlot(world, catalog, courier.PilotId)
        )
        {
            return null;
        }
        var members = world.Filter<Pilot>(pilot => pilot.CorporationId == buyer.Id).ToArray();
        if (members.Any(pilot => ActiveSupplyRun(live, pilot) is not null))
        {
            return null;
        }
        var patrols = members
            .Where(pilot =>
                world.Set<SavedCapsuleerAgent>().Find(pilot.Id) is not null && world.Set<SavedCapsuleerSuspension>().Find(pilot.Id) is null
            )
            .ToArray();
        bool merlin = patrols.Any(pilot => NeedsMerlinReserve(world, pilot));
        IReadOnlyList<ReplacementPart> targets =
            merlin ? Replacement.MerlinParts
            : patrols.Any(pilot => UsesCharge(world, catalog, pilot)) ? [new(Ammo.ChargeType, Ammo.ShipmentQuantity)]
            : [];
        if (targets.Count == 0)
        {
            return null;
        }
        var home = catalog.RequireStation(buyer.StationId);
        var stock = DepotStacks(world, buyer.Id, home.Id).ToLookup(item => item.TypeId);
        var production = world
            .Filter<IndustryJob>(job => job.OutputCorporationId == buyer.Id)
            .Where(job => job.StationId == home.Id)
            .Select(job => job.ProductTypeId)
            .ToHashSet();
        var pending = world
            .Filter<SavedPersonalOrder>(order => order.CorporationId == buyer.Id)
            .Where(order => order.Bid && order.StationId == home.Id && order.Status == PersonalOrderStatus.Open && order.Expires > now)
            .ToLookup(order => order.TypeId);
        var shortages = targets
            .Where(part => !production.Contains(part.TypeId))
            .Select(part =>
                part with
                {
                    Quantity = (int)
                        Math.Max(
                            0,
                            part.Quantity
                                - stock[part.TypeId].Sum(item => (long)item.Quantity)
                                - pending[part.TypeId].Sum(order => (long)order.Remaining)
                        ),
                }
            )
            .Where(part => part.Quantity > 0)
            .ToArray();
        if (shortages.Length == 0)
        {
            return null;
        }

        var destinations = RegionalStockSources(world, catalog, buyer, home, shortages, now);
        return destinations
            .Select(source =>
            {
                var route = CapsuleerTrafficRoutes.ToStation(catalog, home.Id, source.Key, HullClass.Hauler, avoided, Rules.MaximumJumps);
                var purchases = route is null
                    ? []
                    : RegionalStockBudget(
                        world,
                        catalog,
                        courier,
                        buyer,
                        source.Key,
                        shortages.Where(part => source.Value.Contains(part.TypeId))
                    );
                return (Route: route, Purchases: purchases);
            })
            .Where(pickup => pickup.Purchases.Length > 0)
            .OrderByDescending(pickup => pickup.Purchases.Length)
            .ThenBy(pickup => pickup.Route!.Systems.Length)
            .ThenBy(pickup => pickup.Route!.DestinationStationId)
            .Select(pickup => new Collection(
                new(buyer.Id, 0) { RegionalStock = new(home.Id, pickup.Route!.DestinationStationId, pickup.Purchases) },
                pickup.Route!
            ))
            .FirstOrDefault();
    }

    private static bool NeedsMerlinReserve(WorldAction world, Pilot pilot) =>
        pilot.ActiveShip.TypeId == (int)TechOneHullType.Merlin
        || pilot.ActiveShip.TypeId == FleetDefinitions.CapsuleType
            && world
                .Filter<ShipLoss>(loss => loss.CapsuleId == pilot.ActiveShipId)
                .Any(loss => loss.PilotId == pilot.Id && loss.TypeId == (int)TechOneHullType.Merlin);

    private static Dictionary<int, HashSet<int>> RegionalStockSources(
        WorldAction world,
        CatalogDbContext catalog,
        SavedCorporation buyer,
        CatalogStationDefinition home,
        ReplacementPart[] shortage,
        long now
    )
    {
        var nearby = NearbySystems(catalog, home.SolarSystemId, Rules.MaximumJumps);
        Dictionary<int, HashSet<int>> stock = [];
        foreach (var part in shortage)
        {
            long limit = Replacement.LimitPrice(ReferencePrice(catalog, part.TypeId));
            foreach (
                var listing in world
                    .Filter<SavedPersonalOrder>(order => order.TypeId == part.TypeId)
                    .Where(order =>
                        !order.Bid
                        && order.CorporationId != buyer.Id
                        && order.Status == PersonalOrderStatus.Open
                        && order.Expires > now
                        && order.PriceCents <= limit
                        && nearby.Contains(order.SystemId)
                    )
            )
            {
                AddSource(listing.StationId, part.TypeId);
            }
        }
        var recipes = catalog.Definitions.GlobalDefinition(s_recipesByProduct);
        foreach (int system in nearby.Where(system => system != home.SolarSystemId))
        {
            foreach (
                var supplier in world
                    .Filter<SavedCorporation>(row => row.SystemId == system)
                    .Where(row => row.Id != buyer.Id && row.ClosedAt == null)
                    .AsEnumerable()
                    .Where(row => CorporationMembershipStore.BotLed(world, row))
            )
            {
                foreach (
                    var producer in world
                        .Filter<Pilot>(pilot => pilot.CorporationId == supplier.Id)
                        .AsEnumerable()
                        .Where(pilot =>
                            world.Set<SavedCapsuleerAgent>().Find(pilot.Id)?.Career == CapsuleerCareer.Mining
                            && world.Set<SavedCapsuleerSuspension>().Find(pilot.Id) is null
                        )
                )
                {
                    var originals = Hangar(world, producer.Id, supplier.StationId)
                        .Where(item => item.Singleton && item.BlueprintRuns != 0)
                        .Select(item => item.TypeId)
                        .ToHashSet();
                    foreach (
                        var part in shortage.Where(part => recipes[part.TypeId].Any(recipe => originals.Contains(recipe.BlueprintTypeId)))
                    )
                    {
                        AddSource(supplier.StationId, part.TypeId);
                    }
                }
            }
        }
        return stock;

        void AddSource(int id, int type)
        {
            if (
                catalog.Definitions.Stations.GetValueOrDefault(id) is not { } station
                || station.RegionId == home.RegionId
                || !StationCapabilities.Has(catalog, station.OperationId, StationService.Market)
            )
            {
                return;
            }
            if (!stock.TryGetValue(id, out var types))
            {
                types = [];
                stock.Add(id, types);
            }
            types.Add(type);
        }
    }

    private static MarketPurchase[] RegionalStockBudget(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        SavedCorporation buyer,
        int station,
        IEnumerable<ReplacementPart> shortages
    )
    {
        long budget = buyer.BalanceCents / Rules.BudgetDivisor;
        double free = FreeFreightVolume(world, catalog, courier);
        List<MarketPurchase> purchases = [];
        foreach (var part in shortages)
        {
            long price = Replacement.LimitPrice(ReferencePrice(catalog, part.TypeId));
            double volume = PilotInventoryStore.Volume(catalog, part.TypeId);
            if (
                price <= 0
                || volume <= 0
                || free < part.Quantity * volume
                || budget / part.Quantity < price
                || catalog.Read(CatalogData.Client.Types, (long)part.TypeId) is not { Published: 1, MarketGroupID: > 0 }
            )
            {
                continue;
            }
            long fee = PilotMarketStore.SupplyBrokerFee(world, catalog, courier.PilotId, station, price, part.Quantity);
            long escrow = price * part.Quantity;
            if (budget - escrow < fee)
            {
                continue;
            }
            purchases.Add(new(part.TypeId, price, part.Quantity));
            free -= part.Quantity * volume;
            budget -= escrow + fee;
        }
        return purchases.ToArray();
    }

    internal long[] PlaceRegionalStockBids(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        SavedCorporation buyer,
        RegionalStockTrip trip
    )
    {
        var cart = RegionalStockBudget(
            world,
            catalog,
            courier,
            buyer,
            trip.Supplier,
            trip.Purchases.Select(purchase => new ReplacementPart(purchase.TypeId, purchase.Quantity))
        );
        List<long> ids = [];
        foreach (var purchase in cart)
        {
            if (!PilotMarketStore.HasSupplyListingSlot(world, catalog, courier.PilotId))
            {
                break;
            }
            var asks = market.SupplyOrders(world, catalog, trip.Supplier, [purchase.TypeId], courier.PilotId, buyer.Id);
            var quote = QuoteInput(asks.Where(order => order.PriceCents <= purchase.LimitPriceCents), purchase.TypeId, purchase.Quantity);
            market.BuyCorporationStock(
                world,
                catalog,
                courier.PilotId,
                trip.Supplier,
                buyer,
                [quote?.Quantity == purchase.Quantity ? quote : purchase],
                Replacement.OrderDays,
                ids
            );
        }
        return ids.ToArray();
    }

    internal static SavedPersonalOrder[] RegionalStockBids(
        WorldAction world,
        DockedPilot courier,
        CorporationSupplyRun run,
        RegionalStockTrip trip
    ) =>
        (trip.OrderIds ?? [])
            .Select(id => world.Set<SavedPersonalOrder>().Find(id))
            .OfType<SavedPersonalOrder>()
            .Where(bid =>
                bid.Bid && bid.PilotId == courier.PilotId && bid.StationId == trip.Supplier && bid.CorporationId == run.CorporationId
            )
            .ToArray();

    internal bool CancelRegionalStockBids(EconomyTransaction world, CatalogDbContext catalog, SavedPersonalOrder[] bids)
    {
        var outstanding = bids.Where(bid => bid.Status == PersonalOrderStatus.Open).ToArray();
        foreach (var bid in outstanding)
        {
            market.CancelCorporationBid(world, catalog, bid);
        }
        return outstanding.Length > 0;
    }

    internal static FreightManifest? CollectRegionalStock(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        int headquarters,
        SavedPersonalOrder[] bids
    )
    {
        var hangar = Hangar(world, courier.PilotId, courier.StationId).Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        var filled = bids.Select(bid => new ReplacementPart(
                bid.TypeId,
                Math.Min(bid.Entered - bid.Remaining, hangar[bid.TypeId].FirstOrDefault()?.Quantity ?? 0)
            ))
            .Where(part => part.Quantity > 0)
            .ToArray();
        if (
            filled.Sum(part => part.Quantity * PilotInventoryStore.Volume(catalog, part.TypeId))
            > FreeFreightVolume(world, catalog, courier)
        )
        {
            return null;
        }
        return new(
            bids.FirstOrDefault()?.Id ?? 0,
            headquarters,
            filled.Select(part => StowPurchased(world, courier, part.TypeId, part.Quantity)).ToArray()
        );
    }
}
