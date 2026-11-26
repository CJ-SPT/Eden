using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerReplacementRules;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private sealed record ReplacementPurchase(ReplacementKit Kit, ReplacementPart[] Missing, MarketPurchase[] Purchases);

    private static readonly CatalogDefinition<CapsuleerTrafficRoute[]> s_replacementHubRoutes = new(
        "capsuleer-replacement-trade-hub-routes",
        (catalog, source) =>
            catalog
                .Definition(s_tradeHubs, 0)
                .Where(station => station != source)
                .Select(station => CapsuleerTrafficRoutes.CapsuleToTradeHub(catalog, source, station))
                .OfType<CapsuleerTrafficRoute>()
                .OrderBy(route => route.Systems.Length)
                .ThenBy(route => route.DestinationStationId)
                .ToArray()
    );

    internal bool TryBoardOwnedReplacement(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerCareer career,
        long now
    )
    {
        if (
            pilot.Position is not null
            || pilot.InStructure
            || pilot.ShipGroupId != FleetDefinitions.CapsuleGroup
            || ReplacementLoss(world, catalog, pilot.PilotId, pilot.ShipId) is not { } loss
            || LostKit(catalog, loss) is not { } kit
        )
        {
            return false;
        }
        var parts = kit.Parts();
        if (
            !CanReplaceKit(world, catalog, pilot.PilotId, career, kit, parts)
            || MissingParts(
                parts,
                Hangar(world, pilot.PilotId, pilot.StationId).Where(item => !item.Singleton).ToLookup(item => item.TypeId)
            ).Length != 0
        )
        {
            return false;
        }
        BoardKit(world, catalog, pilot, kit, now, replacesShip: loss.ShipId);
        return true;
    }

    internal ReplacementResult ReplaceShip(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot docked,
        CapsuleerCareer career,
        long now,
        long? orderDeadline,
        long[]? orderIds,
        bool hubShopping = false
    )
    {
        long pilotId = docked.PilotId;
        int station = docked.StationId;
        bool funded = CapsuleerRefitFunding.Applies(world, pilotId);
        var corporation = Employer(world, pilotId, docked.CorpId);
        if (docked.Position is not null || docked.InStructure || docked.ShipGroupId != FleetDefinitions.CapsuleGroup)
        {
            return new(false, false, orderDeadline, orderIds);
        }
        var pilot = world.Pilots.Find(pilotId)!;
        if (pilot.ActiveShipId != docked.ShipId || pilot.ActiveShip.Position is not null || pilot.ActiveShip.DockedLocationId != station)
        {
            return new(false, false, orderDeadline, orderIds);
        }

        var loss = ReplacementLoss(world, catalog, pilotId, docked.ShipId);
        bool atTradeHub = CapsuleerEncounterRules.IsTradeHubStation(station) && (hubShopping || corporation is null || loss is null);
        if (!atTradeHub && (loss is null || !funded && corporation is null))
        {
            return new(false, false, orderDeadline, orderIds);
        }
        var kit = loss is null ? null : LostKit(catalog, loss);
        var parts = kit?.Parts() ?? [];
        // Older recovery checkpoints lack order IDs. Match the saved fit or the previous fixed Merlin kit.
        IReadOnlyList<ReplacementPart> legacyParts =
            orderDeadline.HasValue ? parts
            : loss?.TypeId == (int)TechOneHullType.Merlin ? Rules.MerlinParts
            : [];
        var bids = corporation is null
            ? []
            : world
                .Filter<SavedPersonalOrder>(order => order.PilotId == pilotId)
                .Where(order =>
                    order.CorporationId == corporation.Id
                    && order.StationId == station
                    && order.Bid
                    && order.Status == PersonalOrderStatus.Open
                )
                .AsEnumerable()
                .Where(order =>
                    orderIds?.Contains(order.Id)
                    ?? (
                        order.DurationDays == Rules.OrderDays
                        && loss is not null
                        && order.Issued >= loss.Time
                        && order.Issued <= (orderDeadline ?? now)
                        && legacyParts.Any(part => part.TypeId == order.TypeId && order.Entered <= part.Quantity)
                        && order.PriceCents == Rules.LimitPrice(ReferencePrice(catalog, order.TypeId))
                    )
                )
                .OrderBy(order => order.Id)
                .ToArray();
        orderDeadline ??= checked(bids.Select(bid => bid.Issued).DefaultIfEmpty(now).Min() + Rules.BidWindow.Ticks);
        bool usable = kit is not null && CanReplaceKit(world, catalog, pilotId, career, kit, parts);
        var hangar = Hangar(world, pilotId, station).ToLookup(item => item.TypeId);
        var missing = MissingParts(parts, hangar);
        if (atTradeHub)
        {
            CancelReplacementBids(world, catalog, bids);
            var result = ShopReplacement(world, catalog, docked, career, now, now, loss, kit, corporation, hangar, funded, true);
            return result with { MarketChanged = result.MarketChanged || bids.Length > 0 };
        }
        if (usable && missing.Length == 0)
        {
            CancelReplacementBids(world, catalog, bids);
            BoardKit(world, catalog, docked, kit!, now, replacesShip: loss!.ShipId);
            return new(true, bids.Length > 0, null);
        }
        if (funded)
        {
            var result = ShopReplacement(
                world,
                catalog,
                docked,
                career,
                now,
                orderDeadline.Value,
                loss,
                kit,
                corporation,
                hangar,
                funded: true
            );
            if (result.Boarded)
            {
                CancelReplacementBids(world, catalog, bids);
                return result with { MarketChanged = result.MarketChanged || bids.Length > 0 };
            }
            if (corporation is null)
            {
                return result;
            }
        }
        if (!usable || now >= orderDeadline)
        {
            if (bids.Length > 0)
            {
                // Commit the refunds separately so even a failed alternative leaves the old bids closed.
                CancelReplacementBids(world, catalog, bids);
                return new(false, true, now, []);
            }
            return ShopReplacement(world, catalog, docked, career, now, orderDeadline.Value, loss, kit, corporation, hangar, false);
        }

        bool marketChanged = false;
        var depot = DepotStacks(world, corporation!.Id, station).OrderBy(item => item.Id).ToLookup(item => item.TypeId);
        HashSet<int> covered = [];
        List<long> recoveryOrders = [];
        foreach (var bid in bids)
        {
            if (!missing.Any(part => part.TypeId == bid.TypeId && part.Quantity == bid.Remaining) || !covered.Add(bid.TypeId))
            {
                market.CancelCorporationBid(world, catalog, bid);
                marketChanged = true;
                continue;
            }
            recoveryOrders.Add(bid.Id);
        }
        foreach (var part in missing.Where(part => !covered.Contains(part.TypeId)))
        {
            marketChanged |= ProcureReplacementPart(
                world,
                catalog,
                corporation!,
                pilotId,
                station,
                part,
                depot[part.TypeId],
                StationCapabilities.Has(catalog, docked.OperationId, StationService.Market),
                recoveryOrders
            );
        }
        // Purchases and escrow commit on the docked job. The next job works from owned hangar stock.
        return new(false, marketChanged, orderDeadline, recoveryOrders.ToArray());
    }

    private void CancelReplacementBids(EconomyTransaction world, CatalogDbContext catalog, IEnumerable<SavedPersonalOrder> bids)
    {
        foreach (var bid in bids)
        {
            market.CancelCorporationBid(world, catalog, bid);
        }
    }

    private static bool CanReplaceKit(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilot,
        CapsuleerCareer career,
        ReplacementKit kit,
        ReplacementPart[] parts
    ) =>
        SupportedKit(catalog, kit, parts)
        && (
            career != CapsuleerCareer.Mining
            || kit.Slots.Any(slot =>
                slot.TypeId == (int)CapsuleerMiningRules.Laser((CapsuleerMiningHull)kit.HullTypeId)
                && InventoryDefinitions.IsHighSlot(slot.FlagId)
                && kit.OnlineSlots.Contains(slot.FlagId)
            )
        )
        && PilotSkillReader.UsableTypes(world, catalog, pilot, parts.Select(part => part.TypeId).ToArray()).Count == parts.Length;

    private static ReplacementPart[] MissingParts(ReplacementPart[] parts, ILookup<int, OwnedItem> stock) =>
        parts
            .Select(part =>
                part with
                {
                    Quantity = (int)
                        Math.Max(0, part.Quantity - stock[part.TypeId].Where(item => !item.Singleton).Sum(item => (long)item.Quantity)),
                }
            )
            .Where(part => part.Quantity > 0)
            .ToArray();

    private static ShipLoss? ReplacementLoss(EconomyTransaction world, CatalogDbContext catalog, long pilotId, long capsule)
    {
        HashSet<long>? visited = null;
        while (world.Filter<ShipLoss>(loss => loss.CapsuleId == capsule).FirstOrDefault(loss => loss.PilotId == pilotId) is { } loss)
        {
            if (loss.TypeId != FleetDefinitions.CapsuleType && catalog.FittingType(loss.TypeId)?.GroupId != FleetDefinitions.CapsuleGroup)
            {
                return loss;
            }
            if (!(visited ??= []).Add(capsule))
            {
                break;
            }
            capsule = loss.ShipId;
        }
        return null;
    }

    internal CapsuleerTrafficRoute? FindReplacementHub(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerCareer career,
        IReadOnlySet<int>? avoided = null
    )
    {
        if (pilot.InStructure || catalog.FindStation(pilot.StationId) is null)
        {
            return null;
        }
        var loss = ReplacementLoss(world, catalog, pilot.PilotId, pilot.ShipId);
        var original = loss is null ? null : LostKit(catalog, loss);
        var kits = ReplacementKits(world, catalog, pilot.PilotId, career, loss?.TypeId, original, atTradeHub: true).ToArray();
        if (kits.Length == 0)
        {
            return null;
        }
        bool funded = CapsuleerRefitFunding.Applies(world, pilot.PilotId);
        var corporation = Employer(world, pilot.PilotId, pilot.CorpId);
        foreach (var route in catalog.Definition(s_replacementHubRoutes, pilot.StationId))
        {
            int station = route.DestinationStationId;
            if (
                !world.Settings.Capsuleers.CanWorkAtStation(catalog.Definitions, station)
                || avoided is not null && route.Systems.Skip(1).Any(avoided.Contains)
            )
            {
                continue;
            }
            var hangar = Hangar(world, pilot.PilotId, station).ToLookup(item => item.TypeId);
            var depot = ReplacementDepot(world, corporation, station);
            if (QuoteReplacement(world, catalog, pilot.PilotId, station, kits, corporation, hangar, depot, funded) is not null)
            {
                return route;
            }
        }
        return null;
    }

    private static IEnumerable<ReplacementKit> ReplacementKits(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilot,
        CapsuleerCareer career,
        int? lostHull,
        ReplacementKit? original,
        bool atTradeHub
    ) =>
        ReadyKits(catalog, lostHull, career, original, atTradeHub)
            .Where(kit =>
                CanReplaceKit(world, catalog, pilot, career, kit, kit.Parts())
                && (
                    !atTradeHub
                    || (kit.OnlineSlots.Count > 0 || kit.Supplies.Length > 0) && PilotFittingStore.CanOnlineFit(world, catalog, pilot, kit)
                )
            );

    private static ILookup<int, OwnedItem> ReplacementDepot(EconomyTransaction world, SavedCorporation? corporation, int station) =>
        (
            corporation is null ? Enumerable.Empty<OwnedItem>() : DepotStacks(world, corporation.Id, station).OrderBy(item => item.Id)
        ).ToLookup(item => item.TypeId);

    private ReplacementResult ShopReplacement(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot docked,
        CapsuleerCareer career,
        long now,
        long orderDeadline,
        ShipLoss? loss,
        ReplacementKit? original,
        SavedCorporation? corporation,
        ILookup<int, OwnedItem> hangar,
        bool funded,
        bool atTradeHub = false
    )
    {
        int station = docked.StationId;
        long pilot = docked.PilotId;
        var depot = ReplacementDepot(world, corporation, station);
        var kits = ReplacementKits(world, catalog, pilot, career, loss?.TypeId, original, atTradeHub);
        if (QuoteReplacement(world, catalog, pilot, station, kits, corporation, hangar, depot, funded) is not { } plan)
        {
            return new(false, false, Math.Min(now, orderDeadline), []);
        }
        foreach (var part in plan.Missing)
        {
            TransferReplacementStock(world, catalog, pilot, station, part, depot[part.TypeId]);
        }
        if (plan.Purchases.Length > 0)
        {
            var bought =
                funded || corporation is null
                    ? market.BuyBotRefitSupplies(world, catalog, pilot, station, plan.Purchases)
                    : market.BuyCorporationStock(world, catalog, pilot, station, corporation, plan.Purchases);
            if (bought.Trades.Sum(trade => (long)trade.Quantity) != plan.Purchases.Sum(quote => (long)quote.Quantity))
            {
                throw new InvalidDataException("Replacement offers changed before purchase.");
            }
        }
        long? replacedShip = ReferenceEquals(plan.Kit, original) ? loss?.ShipId : null;
        BoardKit(world, catalog, docked, plan.Kit, now, requireOnlineFit: atTradeHub, replacesShip: replacedShip);
        return new(true, plan.Purchases.Length > 0, null);
    }

    private ReplacementPurchase? QuoteReplacement(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilot,
        int station,
        IEnumerable<ReplacementKit> kits,
        SavedCorporation? corporation,
        ILookup<int, OwnedItem> hangar,
        ILookup<int, OwnedItem> depot,
        bool funded
    )
    {
        long budget = funded ? long.MaxValue : corporation?.BalanceCents ?? WalletBalance(world, pilot);
        bool marketAvailable = StationCapabilities.Has(catalog, catalog.RequireStation(station).OperationId, StationService.Market);
        foreach (var kit in kits)
        {
            var missing = MissingParts(kit.Parts(), hangar);
            var toBuy = MissingParts(missing, depot);
            if (toBuy.Length == 0)
            {
                return new(kit, missing, []);
            }
            if (!marketAvailable || budget <= 0 || toBuy.Any(part => !IsMarketSupply(catalog, part.TypeId)))
            {
                continue;
            }
            var asks = market
                .SupplyOrders(world, catalog, station, toBuy.Select(part => part.TypeId).ToArray(), pilot, corporation?.Id)
                .ToLookup(order => order.TypeId);
            List<MarketPurchase> purchases = [];
            decimal total = 0;
            foreach (var part in toBuy)
            {
                long limit = Rules.LimitPrice(ReferencePrice(catalog, part.TypeId));
                if (
                    QuoteInput(
                        asks[part.TypeId].TakeWhile(order => limit == 0 || order.PriceCents <= limit),
                        part.TypeId,
                        part.Quantity,
                        estimateCost: !funded,
                        out decimal cost
                    )
                        is not { } quote
                    || quote.Quantity != part.Quantity
                )
                {
                    break;
                }
                total += funded ? (decimal)quote.LimitPriceCents * quote.Quantity : cost;
                purchases.Add(quote);
            }
            if (purchases.Count != toBuy.Length || total > budget)
            {
                continue;
            }
            return new(kit, missing, purchases.ToArray());
        }
        return null;
    }

    private long BoardKit(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot docked,
        ReplacementKit kit,
        long now,
        bool requireOnlineFit = false,
        long? replacesShip = null
    )
    {
        long pilotId = docked.PilotId;
        int station = docked.StationId;
        var hangar = Hangar(world, pilotId, station).Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        var package = hangar[kit.HullTypeId].First();
        var assembly = PilotFleetStore.Assemble(world, catalog, pilotId, [package.Id], name: null);
        long shipId = assembly.ShipIds.Single();
        var sources = hangar
            .Where(group => group.Key != kit.HullTypeId)
            .ToDictionary(group => group.Key, IReadOnlyList<long> (group) => group.Select(item => item.Id).ToArray());
        var request = new ApplyFittingRequest(shipId, kit.HullTypeId, station, sources, kit.Slots, kit.Supplies, FitRigs: true);
        var pilot = world.Pilots.Find(pilotId)!;
        if (fitting.Apply(world, catalog, pilot, world.Ships.Find(shipId)!, request, kit.OnlineSlots).Missing.Count > 0)
        {
            throw new InvalidDataException("Ship kit inventory changed before fitting.");
        }
        if (
            requireOnlineFit
            && world
                .FittedItems(pilotId, shipId)
                .Any(item => !item.Online && !InventoryDefinitions.IsRigSlot(item.FlagId) && kit.OnlineSlots.Contains(item.FlagId))
        )
        {
            throw new InvalidDataException("The purchased PvP fitting could not stay online.");
        }
        PilotFleetStore.Board(world, catalog, pilotId, shipId, docked.ShipId, now);
        if (kit.Magazines.Length > 0)
        {
            var fitted = world.Filter<OwnedItem>(item => item.LocationId == shipId).ToArray();
            foreach (var charge in kit.Magazines.GroupBy(magazine => magazine.TypeId))
            {
                var modules = charge.Select(magazine => fitted.Single(item => item.FlagId == magazine.Slot).Id).ToArray();
                PilotAmmunitionStore.Load(world, catalog, pilotId, shipId, modules, sources[charge.Key], station, now);
            }
        }
        if (
            replacesShip is long lost
            && CapsuleerCorporations.Organization(world, pilotId) is { EarnedPvpShipId: long earned } organization
            && earned == lost
        )
        {
            CapsuleerCorporations.SaveOrganization(world, pilotId, organization with { EarnedPvpShipId = shipId });
        }
        return shipId;
    }

    private bool ProcureReplacementPart(
        EconomyTransaction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        long pilot,
        int station,
        ReplacementPart part,
        IEnumerable<OwnedItem> depot,
        bool marketAvailable,
        List<long> recoveryOrders
    )
    {
        int remaining = TransferReplacementStock(world, catalog, pilot, station, part, depot);
        long price = Rules.LimitPrice(ReferencePrice(catalog, part.TypeId));
        if (remaining == 0 || !marketAvailable || !IsMarketSupply(catalog, part.TypeId))
        {
            return false;
        }

        long affordable = corporation.BalanceCents / remaining;
        var asks = market.SupplyOrders(world, catalog, station, [part.TypeId], pilot, corporation.Id);
        long limit = price > 0 ? price : affordable / CapsuleerRegionalStockRules.BudgetDivisor;
        var quote = QuoteInput(
            asks.TakeWhile(order => order.PriceCents <= limit),
            part.TypeId,
            remaining,
            estimateCost: true,
            out decimal cost
        );
        if (quote?.Quantity != remaining || cost > corporation.BalanceCents)
        {
            quote = QuoteInput(asks.TakeWhile(order => order.PriceCents <= Math.Min(limit, affordable)), part.TypeId, remaining);
        }
        if (quote is not null)
        {
            var bought = market.BuyCorporationStock(world, catalog, pilot, station, corporation, [quote]);
            if (bought.Trades.Sum(trade => (long)trade.Quantity) != quote.Quantity)
            {
                throw new InvalidDataException("Replacement offers changed before purchase.");
            }
            return true;
        }

        if (price <= 0 || price > affordable || !PilotMarketStore.HasSupplyListingSlot(world, catalog, pilot))
        {
            return false;
        }
        long fee = PilotMarketStore.SupplyBrokerFee(world, catalog, pilot, station, price, remaining);
        if (corporation.BalanceCents - price * remaining < fee)
        {
            return false;
        }
        market.BuyCorporationStock(
            world,
            catalog,
            pilot,
            station,
            corporation,
            [new(part.TypeId, price, remaining)],
            Rules.OrderDays,
            recoveryOrders
        );
        return true;
    }

    private static int TransferReplacementStock(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilot,
        int station,
        ReplacementPart part,
        IEnumerable<OwnedItem> depot
    )
    {
        int remaining = part.Quantity;
        foreach (var stock in depot)
        {
            int quantity = Math.Min(remaining, stock.Quantity);
            InventoryPersistence.MergeOutput(world, catalog, pilot, station, (int)InventoryFlag.Hangar, part.TypeId, quantity);
            stock.Quantity -= quantity;
            remaining -= quantity;
            if (stock.Quantity == 0)
            {
                world.Remove(stock);
            }
            if (remaining == 0)
            {
                break;
            }
        }
        return remaining;
    }
}
