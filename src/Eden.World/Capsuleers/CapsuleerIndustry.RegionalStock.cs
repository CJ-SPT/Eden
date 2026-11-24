using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
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
        long now,
        int emptySupplier = 0
    )
    {
        if (
            (HullClass)courier.ShipGroupId
                is not (HullClass.Hauler or HullClass.BlockadeRunner or HullClass.DeepSpaceTransport or HullClass.Freighter)
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
        var recoveryStations = members
            .Where(pilot => pilot.ActiveShip.TypeId == FleetDefinitions.CapsuleType && pilot.ActiveShip.Position is null)
            .Select(pilot => pilot.ActiveShip.DockedLocationId)
            .Where(station => station != buyer.StationId && station is > 0 and <= int.MaxValue)
            .Distinct()
            .Order()
            .Select(station => (int)station);
        foreach (int destination in recoveryStations.Append(buyer.StationId))
        {
            if (
                catalog.Definitions.Stations.TryGetValue(destination, out var station)
                && PlanRegionalDelivery(world, catalog, courier, buyer, station, members, avoided, now, emptySupplier) is { } pickup
            )
            {
                return pickup;
            }
        }
        return null;
    }

    private static Collection? PlanRegionalDelivery(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        SavedCorporation buyer,
        CatalogStationDefinition delivery,
        Pilot[] members,
        IReadOnlySet<int> avoided,
        long now,
        int emptySupplier
    )
    {
        bool headquarters = delivery.Id == buyer.StationId;
        var targets = FleetStockTargets(world, catalog, buyer.Id, delivery.Id, headquarters, members, now);
        if (targets.Length == 0)
        {
            return null;
        }
        var stock = DepotStacks(world, buyer.Id, delivery.Id).ToLookup(item => item.TypeId);
        var production = world
            .Filter<IndustryJob>(job => job.OutputCorporationId == buyer.Id)
            .Where(job =>
                job.Status == IndustryJobStatus.Installed
                && job.Activity == IndustryActivity.Manufacturing
                && job.OutputFlagId == CorporationStorageRules.MarketDeliveriesFlag
                && (job.OutputLocationId ?? job.StationId) == delivery.Id
            )
            .ToLookup(job => job.ProductTypeId);
        var pending = world
            .Filter<SavedPersonalOrder>(order => order.CorporationId == buyer.Id)
            .Where(order => order.Bid && order.StationId == delivery.Id && order.Status == PersonalOrderStatus.Open && order.Expires > now)
            .ToLookup(order => order.TypeId);
        var shortages = targets
            .Select(part =>
                part with
                {
                    Quantity = (int)
                        Math.Max(
                            0,
                            part.Quantity
                                - stock[part.TypeId].Sum(item => (long)item.Quantity)
                                - production[part.TypeId].Sum(job => (long)job.OutputQuantity)
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

        var destinations = RegionalStockSources(world, catalog, buyer, delivery, shortages, now);
        return destinations
            .Where(source => source.Key != emptySupplier)
            .Select(source =>
            {
                var route = CapsuleerTrafficRoutes.ToStation(
                    catalog,
                    courier.StationId,
                    source.Key,
                    (HullClass)courier.ShipGroupId,
                    avoided,
                    Rules.MaximumJumps,
                    policy: world.Settings.Capsuleers
                );
                var returning = CapsuleerTrafficRoutes.ToStation(
                    catalog,
                    source.Key,
                    delivery.Id,
                    (HullClass)courier.ShipGroupId,
                    avoided,
                    Rules.MaximumJumps,
                    policy: world.Settings.Capsuleers
                );
                var purchases =
                    route is null || returning is null
                        ? []
                        : RegionalStockBudget(
                            world,
                            catalog,
                            courier,
                            buyer,
                            source.Key,
                            shortages
                                .Where(part => source.Value.ContainsKey(part.TypeId))
                                .Select(part => new MarketPurchase(part.TypeId, source.Value[part.TypeId], part.Quantity))
                        );
                return (Route: route, Purchases: purchases);
            })
            .Where(pickup => pickup.Purchases.Length > 0)
            .OrderByDescending(pickup => pickup.Purchases.Length)
            .ThenBy(pickup => pickup.Route!.Systems.Length)
            .ThenBy(pickup => pickup.Route!.DestinationStationId)
            .Select(pickup => new Collection(
                new(buyer.Id, 0)
                {
                    RegionalStock = new(buyer.StationId, pickup.Route!.DestinationStationId, pickup.Purchases)
                    {
                        DeliveryStation = headquarters ? null : delivery.Id,
                    },
                },
                pickup.Route!
            ))
            .FirstOrDefault();
    }

    private static ReplacementPart[] FleetStockTargets(
        EconomyTransaction world,
        CatalogDbContext catalog,
        int corporation,
        int station,
        bool headquarters,
        Pilot[] members,
        long now
    )
    {
        Dictionary<int, int> reserve = [];
        Dictionary<int, long> recovery = [];
        Dictionary<int, long> attrition = [];
        foreach (var pilot in members)
        {
            if (
                world.Set<SavedCapsuleerAgent>().Find(pilot.Id) is not { } agent
                || world.Set<SavedCapsuleerSuspension>().Find(pilot.Id) is not null
            )
            {
                continue;
            }

            if (pilot.ActiveShip.TypeId != FleetDefinitions.CapsuleType)
            {
                if (!headquarters)
                {
                    continue;
                }
                var fit = ActiveKit(world, catalog, pilot);
                if (catalog.FittingType(fit.HullTypeId) is { } hull && HullCatalog.CanAssemble(catalog, hull, hull.CategoryId))
                {
                    foreach (var part in fit.Parts())
                    {
                        reserve[part.TypeId] = Math.Max(reserve.GetValueOrDefault(part.TypeId), part.Quantity);
                    }
                }
                continue;
            }

            if (
                pilot.ActiveShip.DockedLocationId != station
                || world.Filter<ShipLoss>(loss => loss.CapsuleId == pilot.ActiveShipId).FirstOrDefault(loss => loss.PilotId == pilot.Id)
                    is not { } loss
            )
            {
                continue;
            }
            var hangar = Hangar(world, pilot.Id, station).ToLookup(item => item.TypeId);
            var needed = ReadyKits(catalog, loss.TypeId, agent.Career, LostKit(catalog, loss))
                .Select(kit => (Kit: kit, Parts: kit.Parts()))
                .Where(fit => CanReplaceKit(world, catalog, pilot.Id, agent.Career, fit.Kit, fit.Parts))
                .Select(fit => MissingParts(fit.Parts, hangar))
                .FirstOrDefault(parts => parts.All(part => IsMarketSupply(catalog, part.TypeId)));
            if (needed is null)
            {
                continue;
            }
            foreach (var part in needed)
            {
                recovery[part.TypeId] = recovery.GetValueOrDefault(part.TypeId) + part.Quantity;
            }
        }
        if (headquarters)
        {
            var losses = RecentFleetLosses(world, corporation, members, now)
                .GroupBy(loss => loss.TypeId)
                .SelectMany(hull =>
                    hull.OrderByDescending(loss => loss.Time).ThenByDescending(loss => loss.ShipId).Take(Rules.MaximumLossesPerHull)
                );
            foreach (var loss in losses)
            {
                if (LostKit(catalog, loss) is not { } kit || kit.OnlineSlots.Count == 0)
                {
                    continue;
                }

                var parts = kit.Parts();
                if (!SupportedKit(catalog, kit, parts) || parts.Any(part => !IsMarketSupply(catalog, part.TypeId)))
                {
                    continue;
                }
                foreach (var part in parts)
                {
                    attrition[part.TypeId] = attrition.GetValueOrDefault(part.TypeId) + part.Quantity;
                }
            }
        }

        return recovery
            .Keys.Concat(reserve.Keys)
            .Concat(attrition.Keys)
            .Distinct()
            .Where(type => IsMarketSupply(catalog, type))
            .OrderByDescending(type => recovery.ContainsKey(type))
            .ThenByDescending(type => attrition.ContainsKey(type))
            .ThenBy(type => type)
            .Select(type => new ReplacementPart(
                type,
                (int)
                    Math.Min(
                        int.MaxValue,
                        Math.Max(
                            recovery.GetValueOrDefault(type),
                            Math.Max(reserve.GetValueOrDefault(type), attrition.GetValueOrDefault(type))
                        )
                    )
            ))
            .ToArray();
    }

    private static bool IsMarketSupply(CatalogDbContext catalog, int type) =>
        catalog.Read(CatalogData.Client.Types, (long)type) is { Published: 1, MarketGroupID: > 0 };

    private static Dictionary<int, Dictionary<int, long>> RegionalStockSources(
        WorldAction world,
        CatalogDbContext catalog,
        SavedCorporation buyer,
        CatalogStationDefinition delivery,
        ReplacementPart[] shortage,
        long now
    )
    {
        var nearby = NearbySystems(catalog, delivery.SolarSystemId, Rules.MaximumJumps);
        Dictionary<int, Dictionary<int, long>> stock = [];
        var limits = shortage.ToDictionary(part => part.TypeId, part => Replacement.LimitPrice(ReferencePrice(catalog, part.TypeId)));
        foreach (var part in shortage)
        {
            long limit = limits[part.TypeId];
            foreach (
                var listing in world
                    .Filter<SavedPersonalOrder>(order => order.TypeId == part.TypeId)
                    .Where(order =>
                        !order.Bid
                        && order.CorporationId != buyer.Id
                        && order.Status == PersonalOrderStatus.Open
                        && order.Expires > now
                        && order.Remaining > 0
                        && (limit == 0 || order.PriceCents <= limit)
                        && nearby.Contains(order.SystemId)
                    )
            )
            {
                AddSource(listing.StationId, part.TypeId, limit > 0 ? limit : listing.PriceCents);
            }
        }
        var recipes = catalog.Definitions.GlobalDefinition(s_recipesByProduct);
        foreach (int system in nearby.Where(system => system != delivery.SolarSystemId))
        {
            foreach (
                var supplier in world
                    .Filter<SavedCorporation>(row => row.SystemId == system)
                    .Where(row => row.Id != buyer.Id && row.ClosedAt == null)
                    .AsEnumerable()
                    .Where(row => CorporationMembershipStore.BotLed(world, row))
            )
            {
                if (
                    catalog.Definitions.Stations.GetValueOrDefault(supplier.StationId) is not { } facility
                    || !StationCapabilities.Has(catalog, facility.OperationId, StationService.Manufacturing)
                )
                {
                    continue;
                }
                double security = ManufacturingCatalog.Security(catalog, facility.SolarSystemId);
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
                    var available = Hangar(world, producer.Id, supplier.StationId).ToLookup(item => item.TypeId);
                    var invention = catalog.Definitions.GlobalDefinition(s_inventionByBlueprint);
                    bool science = StationCapabilities.Has(catalog, facility.OperationId, StationService.Laboratory);
                    Dictionary<int, int> skills = [];
                    foreach (
                        var part in shortage.Where(part =>
                            limits[part.TypeId] > 0
                            && recipes[part.TypeId]
                                .Any(recipe =>
                                    recipe.Skills.All(Qualified)
                                    && (
                                        available[recipe.BlueprintTypeId].Any(item => item.BlueprintRuns != 0)
                                        || science && invention[recipe.BlueprintTypeId].Any(HasInventionSource)
                                    )
                                )
                            && ManufacturingCatalog.Allowed(catalog, part.TypeId, security)
                        )
                    )
                    {
                        AddSource(supplier.StationId, part.TypeId, limits[part.TypeId]);
                    }

                    bool Qualified(ManufacturingRequiredSkill skill)
                    {
                        if (!skills.TryGetValue(skill.TypeId, out int level))
                        {
                            level = PilotSkillReader.Level(world, catalog, producer.Id, skill.TypeId);
                            skills.Add(skill.TypeId, level);
                        }
                        return level >= skill.Level || skill == s_industryQualification;
                    }

                    bool HasInventionSource(FactoryInvention route) =>
                        route.Invention.Skills.All(Qualified)
                        && available[route.SourceType]
                            .Any(item =>
                                route.Relic
                                    ? item.Quantity > 0
                                    : item.BlueprintRuns > 0 || item.BlueprintRuns < 0 && route.Copying?.Skills.All(Qualified) == true
                            );
                }
            }
        }
        return stock;

        void AddSource(int id, int type, long limit)
        {
            if (
                catalog.Definitions.Stations.GetValueOrDefault(id) is not { } station
                || id == delivery.Id
                || delivery.Id == buyer.StationId && station.RegionId == delivery.RegionId
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
            if (!types.TryGetValue(type, out long current) || limit < current)
            {
                types[type] = limit;
            }
        }
    }

    private static MarketPurchase[] RegionalStockBudget(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        SavedCorporation buyer,
        int station,
        IEnumerable<MarketPurchase> shortages
    )
    {
        long budget = buyer.BalanceCents / Rules.BudgetDivisor;
        double free = FreeFreightVolume(world, catalog, courier);
        List<MarketPurchase> purchases = [];
        foreach (var part in shortages)
        {
            long price = part.LimitPriceCents;
            double volume = PilotInventoryStore.Volume(catalog, part.TypeId);
            if (price <= 0 || volume <= 0 || !IsMarketSupply(catalog, part.TypeId))
            {
                continue;
            }
            int units = (int)Math.Min(part.Quantity, Math.Min(budget / price, Math.Max(0, Math.Floor(free / volume))));
            if (units <= 0)
            {
                continue;
            }
            long fee = PilotMarketStore.SupplyBrokerFee(world, catalog, courier.PilotId, station, price, units);
            if (budget - price * units < fee)
            {
                units = fee < budget ? (int)Math.Min(units, (budget - fee) / price) : 0;
            }
            if (units <= 0)
            {
                continue;
            }
            fee = PilotMarketStore.SupplyBrokerFee(world, catalog, courier.PilotId, station, price, units);
            purchases.Add(new(part.TypeId, price, units));
            free -= units * volume;
            budget -= price * units + fee;
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
        var cart = RegionalStockBudget(world, catalog, courier, buyer, trip.Supplier, trip.Purchases);
        List<long> ids = [];
        foreach (var purchase in cart)
        {
            if (!PilotMarketStore.HasSupplyListingSlot(world, catalog, courier.PilotId))
            {
                break;
            }
            var asks = market.SupplyOrders(world, catalog, trip.Supplier, [purchase.TypeId], courier.PilotId, buyer.Id);
            var quote = QuoteInput(asks.Where(order => order.PriceCents <= purchase.LimitPriceCents), purchase.TypeId, purchase.Quantity);
            bool priced = ReferencePrice(catalog, purchase.TypeId) > 0;
            if (!priced && quote is null)
            {
                continue;
            }
            market.BuyCorporationStock(
                world,
                catalog,
                courier.PilotId,
                trip.Supplier,
                buyer,
                [!priced || quote?.Quantity == purchase.Quantity ? quote! : purchase],
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
                (int)Math.Min(bid.Entered - bid.Remaining, hangar[bid.TypeId].Sum(item => (long)item.Quantity))
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
