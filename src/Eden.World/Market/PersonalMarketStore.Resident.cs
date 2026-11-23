using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Corporations;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    private static void Event(
        WorldAction world,
        SavedPersonalOrder order,
        MarketOrderChangeKind kind,
        IReadOnlyList<InventoryChange>? changes = null
    )
    {
        world.Add(
            new SavedMarketChange
            {
                PilotId = order.PilotId,
                Payload = JsonSerializer.Serialize(new PersonalMarketChange(0, order.Snapshot(), kind, changes ?? [])),
            }
        );
    }

    private void Payment(
        WorldAction world,
        SavedPersonalOrder order,
        WalletEntryType kind,
        long amount,
        long counterparty = 0,
        long? contextId = null
    )
    {
        if (amount == 0)
        {
            return;
        }
        if (order.CorporationId is int corporation)
        {
            CorporationWalletStore.Pay(world, corporation, counterparty, contextId ?? order.Id, kind, amount, Now);
            return;
        }
        var wallet = Wallet(world, order.PilotId);
        long balance = checked(wallet.Cents + amount);
        if (balance < 0)
        {
            throw new InvalidDataException("You do not have enough ISK for this order and its fees.");
        }
        wallet.Cents = balance;
        world.Add(
            new SavedMarketPayment
            {
                PilotId = order.PilotId,
                CounterpartyId = counterparty,
                OrderId = order.Id,
                Kind = kind,
                AmountCents = amount,
                BalanceCents = balance,
                Time = Now,
            }
        );
    }

    private static decimal BrokerRate(WorldAction world, CatalogDbContext catalog, long pilotId, CatalogStationDefinition station)
    {
        long faction = catalog.Read(CatalogData.Client.Npccorporations, (long)station.OwnerId)?.FactionID ?? 0;
        decimal Standing(long owner) => (decimal)(world.Set<SavedNpcStanding>().Find(pilotId, owner)?.Value ?? 0);
        return PersonalMarketRules.BrokerRate(
            PilotSkillReader.Level(world, catalog, pilotId, (int)MarketSkill.BrokerRelations),
            Standing(station.OwnerId),
            Standing(faction)
        );
    }

    private static bool HasListingSlot(WorldAction world, CatalogDbContext catalog, long pilotId)
    {
        int Level(MarketSkill skill) => PilotSkillReader.Level(world, catalog, pilotId, (int)skill);
        int limit = PersonalMarketRules.OrderLimit(
            Level(MarketSkill.Trade),
            Level(MarketSkill.Retail),
            Level(MarketSkill.Wholesale),
            Level(MarketSkill.Tycoon)
        );
        return world.Filter<SavedPersonalOrder>(o => o.PilotId == pilotId).Count(o => o.Status == PersonalOrderStatus.Open) < limit;
    }

    private SavedPersonalOrder Incoming(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        CatalogStationDefinition station,
        int typeId,
        bool bid,
        long price,
        int quantity,
        int days,
        int? corporationId = null
    )
    {
        PersonalMarketRules.Validate(quantity, price, days);
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        if (type is not { Published: 1, MarketGroupID: > 0 } || catalog.FittingType(typeId) is null)
        {
            throw new InvalidDataException("This item is not eligible for the market.");
        }
        var order = new SavedPersonalOrder
        {
            PilotId = pilot.Id,
            CorporationId = corporationId,
            StationId = station.Id,
            SystemId = station.SolarSystemId,
            RegionId = station.RegionId,
            TypeId = typeId,
            Bid = bid,
            PriceCents = price,
            Remaining = quantity,
            Entered = quantity,
            Issued = Now,
            Modified = Now,
            Expires = checked(Now + TimeSpan.FromDays(days).Ticks),
            DurationDays = days,
        };
        if (days > 0)
        {
            if (!HasListingSlot(world, catalog, pilot.Id))
            {
                throw new InvalidDataException("Train trading skills or close an existing market order first.");
            }
            order.Id = world.Next("WorldPersonalOrder", PersonalMarketRules.FirstOrderId);
            // Save once before the next batch entry chooses its ID.
            world.Add(order);
            world.SaveChanges();
            Payment(
                world,
                order,
                WalletEntryType.BrokersFee,
                -PersonalMarketRules.BrokerFee(price, quantity, BrokerRate(world, catalog, pilot.Id, station)),
                station.OwnerId
            );
            if (bid)
            {
                order.EscrowCents = checked(price * quantity);
                Payment(world, order, WalletEntryType.MarketEscrow, -order.EscrowCents);
            }
        }
        return order;
    }

    private List<MarketExecution> MatchOrders(
        WorldAction world,
        CatalogDbContext catalog,
        SavedPersonalOrder incoming,
        List<InventoryChange> changes,
        bool npcDemandOnly = false,
        long? specificOrder = null,
        bool fundedRefit = false
    )
    {
        long now = Now;
        var resident = world as EconomyTransaction;
        var personal =
            npcDemandOnly ? []
            : resident is not null ? ResidentPersonalMatches(resident, incoming, specificOrder, now)
            : world
                .Set<SavedPersonalOrder>()
                .Where(o =>
                    o.StationId == incoming.StationId
                    && o.TypeId == incoming.TypeId
                    && o.Bid != incoming.Bid
                    && o.Status == PersonalOrderStatus.Open
                    && o.Expires > now
                    && o.PilotId != incoming.PilotId
                    && (incoming.CorporationId == null || incoming.CorporationId != o.CorporationId)
                    && (specificOrder == null || o.Id == specificOrder)
                )
                .ToArray();
        long cutoff = clock.GetUtcNow().AddDays(-MarketDefinitions.NpcOrderDays).UtcDateTime.ToFileTimeUtc();
        var kind = MarketKind(world);
        bool progressive = RegionalMarketStore.Enabled(world);
        var npcs =
            specificOrder is not null ? []
            : resident is not null ? ResidentNpcMatches(resident, incoming, kind, progressive, npcDemandOnly, cutoff, now)
            : world
                .Set<NpcMarketOrder>()
                .Where(o =>
                    (
                        o.StationId == incoming.StationId
                        || o.Kind == NpcMarketKind.Standard && !incoming.Bid && o.RegionId == incoming.RegionId
                    )
                    && o.TypeId == incoming.TypeId
                    && o.Bid != incoming.Bid
                    && o.Issued > cutoff
                    && o.Issued + (long)o.DurationDays * TimeSpan.TicksPerDay > now
                    && (
                        progressive
                            ? o.Kind == NpcMarketKind.Institutional
                                || o.Kind == NpcMarketKind.Regional && (incoming.DurationDays == 0 || !incoming.Bid)
                            : o.Kind == kind
                    )
                    && (!npcDemandOnly || o.Kind == NpcMarketKind.Regional && o.Bid)
                )
                .ToArray();
        var candidates = personal
            .Where(order => order.Remaining > 0)
            .Select(order => new Candidate(order, null))
            .Concat(
                npcs.Where(o => o.Remaining > 0 && (o.Kind != NpcMarketKind.Standard || StandardBidReaches(catalog, o, incoming)))
                    .Select(o => new Candidate(null, o))
            )
            .Where(o => PersonalMarketRules.Crosses(incoming.Bid, incoming.PriceCents, o.Price))
            .OrderBy(o => incoming.Bid ? o.Price : -o.Price)
            .ThenBy(o => o.Issued)
            .ThenBy(o => o.Id)
            .ToArray();
        List<MarketExecution> trades = [];
        foreach (var candidate in candidates)
        {
            if (incoming.Remaining == 0)
            {
                break;
            }
            if (
                candidate.Npc is { Kind: NpcMarketKind.Standard } liveOrder
                && !StandardMarketRules.CanFill(incoming.Remaining, liveOrder.Remaining, liveOrder.MinimumVolume)
            )
            {
                continue;
            }
            var fill = PersonalMarketRules.Match(
                incoming.Bid,
                incoming.PriceCents,
                incoming.Remaining,
                candidate.Price,
                candidate.Remaining,
                candidate.Personal is null
            );
            int quantity = fill.Quantity;
            long price = fill.PriceCents;
            long gross = fill.GrossCents;
            long buyerId = incoming.Bid ? incoming.PilotId : candidate.Personal?.PilotId ?? candidate.Owner;
            long sellerId = incoming.Bid ? candidate.Personal?.PilotId ?? candidate.Owner : incoming.PilotId;
            var buyerOrder = incoming.Bid ? incoming : candidate.Personal;
            var sellerOrder = incoming.Bid ? candidate.Personal : incoming;
            var buyerWallet = incoming.Bid || candidate.Personal is not null ? Wallet(world, buyerId) : null;
            var sellerWallet = !incoming.Bid || candidate.Personal is not null ? Wallet(world, sellerId) : null;
            bool escrowed = buyerOrder is { DurationDays: > 0 };
            if (buyerOrder is not null && buyerWallet is not null)
            {
                if (escrowed)
                {
                    long reserved = fill.ReservedCents;
                    buyerOrder.EscrowCents = checked(buyerOrder.EscrowCents - reserved);
                    Payment(world, buyerOrder, WalletEntryType.MarketEscrow, fill.RefundCents);
                }
                else if (buyerOrder.CorporationId.HasValue)
                {
                    Payment(
                        world,
                        buyerOrder,
                        WalletEntryType.MarketTransaction,
                        -gross,
                        sellerOrder?.CorporationId ?? sellerId,
                        buyerOrder.Id == 0 ? candidate.Id : buyerOrder.Id
                    );
                }
                else if (!fundedRefit)
                {
                    if (buyerWallet.Cents < gross)
                    {
                        throw new InvalidDataException("You do not have enough ISK for this purchase.");
                    }
                    buyerWallet.Cents -= gross;
                }
                // A funded refit pays only this matched fill. Nothing is credited to the buyer's wallet.
            }
            long tax = sellerWallet is null ? 0 : MarketRules.Fill(quantity, quantity, price, TaxRate(world, catalog, sellerId)).TaxCents;
            if (sellerOrder?.CorporationId is not null)
            {
                Payment(world, sellerOrder, WalletEntryType.MarketTransaction, gross - tax, buyerOrder?.CorporationId ?? buyerId);
            }
            else if (sellerWallet is not null)
            {
                sellerWallet.Cents = checked(sellerWallet.Cents + gross - tax);
            }
            InventoryChange? delivery = buyerWallet is null
                ? null
                : InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    buyerId,
                    incoming.StationId,
                    (int)InventoryFlag.Hangar,
                    incoming.TypeId,
                    quantity
                );
            if (incoming.Bid && delivery is not null)
            {
                changes.Add(delivery);
            }
            long orderId = candidate.Id;
            MarketExecution Execution(long pilotId, bool buy, long balance, long? walletAmount) =>
                world
                    .Add(
                        new MarketExecution
                        {
                            PilotId = pilotId,
                            CounterpartyId = buy ? sellerOrder?.CorporationId ?? sellerId : buyerOrder?.CorporationId ?? buyerId,
                            OrderId = orderId,
                            StationId = incoming.StationId,
                            TypeId = incoming.TypeId,
                            IsBuy = buy,
                            Quantity = quantity,
                            PriceCents = price,
                            TaxCents = buy ? 0 : tax,
                            BalanceCents = balance,
                            Time = now,
                            WalletAmountCents = walletAmount,
                        }
                    )
                    .Entity;
            if (buyerWallet is not null)
            {
                var execution = Execution(
                    buyerId,
                    true,
                    buyerWallet.Cents,
                    fundedRefit || escrowed || buyerOrder?.CorporationId is not null ? 0 : null
                );
                if (incoming.Bid)
                {
                    trades.Add(execution);
                }
            }
            if (sellerWallet is not null)
            {
                var execution = Execution(sellerId, false, sellerWallet.Cents, sellerOrder?.CorporationId is not null ? 0 : null);
                if (!incoming.Bid)
                {
                    trades.Add(execution);
                }
            }
            incoming.Remaining -= quantity;
            if (candidate.Personal is { } resting)
            {
                resting.Remaining -= quantity;
                if (resting.Remaining == 0)
                {
                    resting.Status = PersonalOrderStatus.Filled;
                }
                Event(world, resting, MarketOrderChangeKind.Settled, !incoming.Bid && delivery is not null ? [delivery] : []);
            }
            else if (candidate.Npc is { } npc)
            {
                if (npc.Kind == NpcMarketKind.Standard)
                {
                    npc.Remaining -= quantity;
                    continue;
                }
                if (npc.Kind == NpcMarketKind.Regional)
                {
                    npc.Remaining -= quantity;
                    RegionalMarketStore.Requote(world, npc);
                    continue;
                }
                npc.Remaining =
                    npc.Bid ? npc.Remaining - quantity
                    : npc.Kind == NpcMarketKind.Institutional
                        ? world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.InstitutionalStock
                    : TestingMarketRules.Stock;
            }
        }
        if (incoming.Remaining == 0)
        {
            incoming.Status = PersonalOrderStatus.Filled;
        }
        return trades;
    }

    private static SavedPersonalOrder[] ResidentPersonalMatches(
        EconomyTransaction world,
        SavedPersonalOrder incoming,
        long? specificOrder,
        long now
    )
    {
        var matches = world
            .StationPersonalOrders(incoming.StationId, incoming.TypeId)
            .Where(order =>
                order.Bid != incoming.Bid
                && order.Status == PersonalOrderStatus.Open
                && order.Expires > now
                && order.PilotId != incoming.PilotId
                && (incoming.CorporationId == null || incoming.CorporationId != order.CorporationId)
                && (specificOrder == null || order.Id == specificOrder)
                && order.Remaining > 0
                && PersonalMarketRules.Crosses(incoming.Bid, incoming.PriceCents, order.PriceCents)
            )
            .Select(order => order.Id)
            .ToArray();
        var writable = world.Set<SavedPersonalOrder>();
        return Array.ConvertAll(matches, id => writable.Find(id)!);
    }

    private static NpcMarketOrder[] ResidentNpcMatches(
        EconomyTransaction world,
        SavedPersonalOrder incoming,
        NpcMarketKind kind,
        bool progressive,
        bool npcDemandOnly,
        long cutoff,
        long now
    )
    {
        var available = world.StationMarketOrders(incoming.StationId, incoming.TypeId);
        if (!progressive && kind == NpcMarketKind.Standard && !incoming.Bid && !npcDemandOnly)
        {
            available = available.Concat(
                world.RegionalOrders(incoming.RegionId, incoming.TypeId).Where(order => order.StationId != incoming.StationId)
            );
        }
        // Finish snapshot reads before opening selected rows in the transaction's writable overlay.
        var matches = available
            .Where(order =>
                order.Bid != incoming.Bid
                && order.Remaining > 0
                && order.Issued > cutoff
                && order.Issued + (long)order.DurationDays * TimeSpan.TicksPerDay > now
                && (
                    progressive
                        ? order.Kind == NpcMarketKind.Institutional
                            || order.Kind == NpcMarketKind.Regional && (incoming.DurationDays == 0 || !incoming.Bid)
                        : order.Kind == kind
                )
                && (!npcDemandOnly || order.Kind == NpcMarketKind.Regional && order.Bid)
                && PersonalMarketRules.Crosses(incoming.Bid, incoming.PriceCents, order.PriceCents)
            )
            .Select(order => order.Id)
            .ToArray();
        var writable = world.Set<NpcMarketOrder>();
        return Array.ConvertAll(matches, id => writable.Find(id)!);
    }

    internal MarketResult BuyBotRefitSupplies(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int stationId,
        IReadOnlyList<MarketPurchase> items
    ) => ExecuteBuyCore(world, catalog, pilotId, stationId, items, 0, null, null, CapsuleerRefitFunding.Applies(world, pilotId));

    internal MarketResult ExecuteBuy(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int stationId,
        IReadOnlyList<MarketPurchase> items,
        int days,
        int? corporationId = null,
        List<long>? placedOrders = null
    ) => ExecuteBuyCore(world, catalog, pilotId, stationId, items, days, corporationId, placedOrders, false);

    private MarketResult ExecuteBuyCore(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int stationId,
        IReadOnlyList<MarketPurchase> items,
        int days,
        int? corporationId,
        List<long>? placedOrders,
        bool fundedRefit
    )
    {
        var pilot = Pilot(world, pilotId);
        // Immediate purchases need no remote-order skill; goods stay at the selling station.
        var station = days == 0 ? PurchaseStation(world, catalog, pilot, stationId) : TradeStation(catalog, pilot, stationId);
        if (items.Count == 0 || items.Select(i => i.TypeId).Distinct().Count() != items.Count)
        {
            throw new InvalidDataException("Select distinct market types.");
        }
        foreach (int type in items.Select(i => i.TypeId))
        {
            Seed(
                world,
                catalog,
                station,
                type,
                scopedDevelopmentSupply: pilot.AccountId == Capsuleers.CapsuleerCommunityStore.MinerAccountId
            );
        }
        List<InventoryChange> changes = [];
        List<MarketExecution> trades = [];
        foreach (var request in items)
        {
            var order = Incoming(
                world,
                catalog,
                pilot,
                station,
                request.TypeId,
                true,
                request.LimitPriceCents,
                request.Quantity,
                days,
                corporationId
            );
            int start = changes.Count;
            var fills = MatchOrders(world, catalog, order, changes, fundedRefit: fundedRefit);
            if (days == 0 && fills.Count == 0)
            {
                throw new InvalidDataException("No matching station order remains at that price. Refresh the market quote.");
            }
            trades.AddRange(fills);
            if (days > 0)
            {
                placedOrders?.Add(order.Id);
                Event(world, order, MarketOrderChangeKind.Placed, changes.Skip(start).ToArray());
            }
        }
        PilotTaskTrackingStore.Record(world, catalog, pilotId, Now, TaskEvents.Bought, trades.Count);
        world.SaveChanges();
        return new(Wallet(world, pilotId).Cents, trades.Select(Read).ToArray(), changes, station.SolarSystemId, station.RegionId);
    }

    internal MarketResult ExecuteSell(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        IReadOnlyList<MarketSale> items,
        int days,
        bool skipUnmatched = false
    )
    {
        var pilot = Pilot(world, pilotId);
        var station = TradeStation(catalog, pilot, pilot.ActiveShip.StationId);
        if (items.Count == 0 || items.Select(i => i.ItemId).Distinct().Count() != items.Count)
        {
            throw new InvalidDataException("Select distinct owned stacks.");
        }
        foreach (int type in items.Select(i => i.TypeId).Distinct())
        {
            Seed(world, catalog, station, type);
        }
        List<InventoryChange> changes = [];
        List<MarketExecution> trades = [];
        foreach (var request in items)
        {
            SettleRestingSales(
                world,
                catalog,
                station.Id,
                request.TypeId,
                PersonalMarketRules.MaintenanceBatch,
                maximumAsk: days == 0 ? long.MaxValue : request.LimitPriceCents,
                requireComplete: true
            );
            var item = InventoryPersistence.OwnedStack(world, pilotId, request.ItemId);
            if (
                item is null
                || item.TypeId != request.TypeId
                || item.Singleton
                || item.CrystalDamage != 0
                || request.StationId != station.Id
                || request.Quantity > item.Quantity
                || PilotIndustryStore.IsInstalled(world, item.Id)
                || !(
                    item.LocationId == station.Id && item.FlagId == (int)InventoryFlag.Hangar
                    || item.LocationId == pilot.ActiveShipId
                        && item.FlagId is (int)InventoryFlag.Cargo or (int)InventoryFlag.GeneralMiningHold
                )
            )
            {
                throw new InvalidDataException("The selected stack or quantity is unavailable in your hangar or cargo.");
            }
            var order = Incoming(world, catalog, pilot, station, request.TypeId, false, request.LimitPriceCents, request.Quantity, days);
            var before = PilotInventoryStore.Read(catalog, item);
            var fills = MatchOrders(world, catalog, order, changes);
            if (days == 0 && fills.Count == 0)
            {
                if (skipUnmatched)
                {
                    continue;
                }
                throw new InvalidDataException("No matching station order remains at that price. Refresh the market quote.");
            }
            item.Quantity -= days > 0 ? request.Quantity : request.Quantity - order.Remaining;
            var change = new InventoryChange(before with { Quantity = item.Quantity }, before);
            changes.Add(change);
            if (item.Quantity == 0)
            {
                world.Remove(item);
            }
            trades.AddRange(fills);
            if (days > 0)
            {
                Event(world, order, MarketOrderChangeKind.Placed, [change]);
            }
        }
        PilotTaskTrackingStore.Record(world, catalog, pilotId, Now, TaskEvents.Sold, trades.Count);
        world.SaveChanges();
        return new(Wallet(world, pilotId).Cents, trades.Select(Read).ToArray(), changes, station.SolarSystemId, station.RegionId);
    }

    private SavedPersonalOrder OwnedOrder(WorldAction world, CatalogDbContext catalog, long pilotId, long id)
    {
        var order = world.Set<SavedPersonalOrder>().Find(id);
        if (
            order is null
            || order.CorporationId.HasValue
            || order.PilotId != pilotId
            || order.Status != PersonalOrderStatus.Open
            || order.Expires <= Now
        )
        {
            throw new InvalidDataException("This personal order is no longer available.");
        }
        _ = TradeStation(catalog, Pilot(world, pilotId), order.StationId);
        return order;
    }

    internal void CancelPersonalSupplyBid(EconomyTransaction world, CatalogDbContext catalog, SavedPersonalOrder bid)
    {
        if (bid.Bid && bid.CorporationId is null && bid.Status == PersonalOrderStatus.Open)
        {
            Close(world, catalog, bid, PersonalOrderStatus.Cancelled);
        }
    }

    private void Close(WorldAction world, CatalogDbContext catalog, SavedPersonalOrder order, PersonalOrderStatus status)
    {
        List<InventoryChange> changes = [];
        if (order.Bid)
        {
            Payment(world, order, WalletEntryType.MarketEscrow, order.EscrowCents);
            order.EscrowCents = 0;
        }
        else if (order.Remaining > 0)
        {
            changes.Add(
                InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    order.CorporationId ?? order.PilotId,
                    order.StationId,
                    order.CorporationId.HasValue ? CorporationStorageRules.MarketDeliveriesFlag : (int)InventoryFlag.Hangar,
                    order.TypeId,
                    order.Remaining
                )
            );
        }
        order.Status = status;
        Event(world, order, MarketOrderChangeKind.Settled, changes);
    }

    internal int Expire(WorldAction world, CatalogDbContext catalog, int limit = PersonalMarketRules.MaintenanceBatch)
    {
        long now = Now;
        var orders = world
            .Set<SavedPersonalOrder>()
            .Where(o => o.Status == PersonalOrderStatus.Open && o.Expires <= now)
            .OrderBy(o => o.Expires)
            .ThenBy(o => o.Id)
            .Take(limit)
            .ToArray();
        foreach (var order in orders)
        {
            Close(world, catalog, order, PersonalOrderStatus.Expired);
        }
        world.SaveChanges();
        return orders.Length;
    }
}
