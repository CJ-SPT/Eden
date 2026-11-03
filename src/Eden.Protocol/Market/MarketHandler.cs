using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

[Injectable]
internal sealed class MarketHandler(IPilotMarket market, TimeProvider clock, IPilotCombat combat, IPilotRepair repair)
    : ISelectedPilotCallHandler
{
    private static double Isk(long cents) => (double)cents / MarketDefinitions.CentsPerIsk;

    private static MarketOrderRow Row(MarketOrder order, int duration = MarketDefinitions.NpcOrderDays) => new(order, duration);

    private static MarketOrderRows Rows(IEnumerable<MarketOrder> orders) => new(orders.ToArray());

    private MarketHistoryPair HistoryPair(IEnumerable<MarketHistoryDay> days) =>
        new(days.ToArray(), clock.GetUtcNow().UtcDateTime.Date.ToFileTimeUtc());

    private CachedMarketOrders CachedOrders(IBlueWritable result) => new(result, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());

    private BlueList<MarketOrderRow> Changed(PilotCallContext context, MarketResult result)
    {
        foreach (var change in result.Changes)
        {
            InventoryHandler.NotifyItemChange(context, change);
        }
        context.Notifications.Add(new AccountBalanceChanged(context.Pilot.PilotId, Isk(result.BalanceCents)));
        var orders = result
            .Trades.Select(trade =>
                Row(
                    new(
                        MarketDefinitions.FirstExecutionOrderId + trade.Id,
                        trade.StationId,
                        context.Pilot.SystemId,
                        context.Pilot.RegionId,
                        context.Pilot.PilotId,
                        trade.TypeId,
                        trade.IsBuy,
                        trade.PriceCents,
                        0,
                        trade.Quantity,
                        trade.Time
                    ),
                    0
                )
            )
            .ToList();
        // Completed immediate orders invalidate the client's order and transaction
        // caches. They are executions, not outstanding personal listings.
        context.Notifications.Add(new OwnOrdersChanged(orders));
        foreach (var change in result.Changes.Where(c => c.Previous is null))
        {
            context.Notifications.Add(new MarketItemReceived(change.Item));
        }
        return new(orders);
    }

    public object? Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "account")
        {
            return Account(context, method, args);
        }
        bool check = service == "corpStationMgr" && method == "DoStandingCheckForStationService" && args is [(long)StationService.Market];
        if (service != "marketProxy" && !check)
        {
            return PilotCallRouter.Unsupported;
        }
        try
        {
            if (check)
            {
                market.CheckStation(context.Pilot.PilotId, context.Pilot.StationId);
                return null;
            }
            switch (method)
            {
                case "StartupCheck":
                    return null;
                case "GetOrders" when args is [long type] && type is > 0 and <= int.MaxValue:
                    var book = market.Orders(context.Pilot.PilotId).Where(o => o.TypeId == type).ToArray();
                    return CachedOrders(new MarketOrderBook(book.Where(o => !o.Bid).ToArray(), book.Where(o => o.Bid).ToArray()));
                case "GetStationAsks":
                case "GetSystemAsks":
                case "GetRegionBest":
                    return new BlueMap<long, MarketOrderRow>(
                        market
                            .Orders(context.Pilot.PilotId)
                            .Where(o =>
                                !o.Bid
                                && (
                                    method == "GetRegionBest"
                                    || (
                                        method == "GetStationAsks"
                                            ? o.StationId == context.Pilot.StationId
                                            : o.SystemId == context.Pilot.SystemId
                                    )
                                )
                            )
                            .GroupBy(o => o.TypeId)
                            .ToDictionary(g => (long)g.Key, g => Row(g.OrderBy(o => o.PriceCents).ThenBy(o => o.Id).First()))
                    );
                case "GetCharOrders":
                case "GetCorporationOrders":
                case "GetMarketOrderHistory":
                    return Rows([]);
                case "GetOldPriceHistory" or "GetNewPriceHistory" when args is [long type] && type is > 0 and <= int.MaxValue:
                    return HistoryPair(market.History(context.Pilot.PilotId, [(int)type])).For(method == "GetNewPriceHistory");
                case "GetHistoryForManyTypeIDs" when args is [HashSet<long> types] && types.All(t => t is > 0 and <= int.MaxValue):
                    var days = market.History(context.Pilot.PilotId, types.Select(t => (int)t).ToArray());
                    return new BlueMap<long, MarketHistoryPair>(
                        types.ToDictionary(t => t, t => HistoryPair(days.Where(d => d.TypeId == t)))
                    );
                case "GetPlexHistory" when args.Length == 0:
                    return new BlueMap<long, MarketHistoryPair>(
                        new Dictionary<long, MarketHistoryPair> { [MarketDefinitions.PlexTypeId] = HistoryPair([]) }
                    );
                case "PlaceMultiSellOrder" when args is [List<object?> entries, false, 0L, _]:
                    var sales = entries.Select(MarketRequestWire.Sale).ToArray();
                    Changed(context, market.Sell(context.Pilot.PilotId, sales));
                    return true;
                case "PlaceBuyOrder"
                    when args
                        is [long station, long buyType, var price, long quantity, (long)MarketDefinitions.StationRange, 1L, 0L, false, _]:
                    Changed(
                        context,
                        market.Buy(
                            context.Pilot.PilotId,
                            checked((int)station),
                            [new(checked((int)buyType), MarketRequestWire.Price(price), checked((int)quantity))]
                        )
                    );
                    return null;
                case "BuyMultipleItems" when args is [long destination, List<object?> entries, false]:
                    var purchases = entries.Select(MarketRequestWire.Purchase).ToArray();
                    return Changed(context, market.Buy(context.Pilot.PilotId, checked((int)destination), purchases));
                case "CharGetTransactions" when args is [null or long]:
                    return new BlueList<MarketTransactionReply>(
                        market
                            .Transactions(context.Pilot.PilotId)
                            .Where(trade => args[0] is not long from || trade.Time < from)
                            .Take(1000)
                            .Select(trade => new MarketTransactionReply(trade, context.Pilot.PilotId))
                            .ToArray()
                    );
                case "PlaceMultiSellOrder":
                case "PlaceBuyOrder":
                case "BuyMultipleItems":
                    return new PilotCallRouter.Error("Eden currently supports immediate personal trades at your docked station.");
                default:
                    return PilotCallRouter.Unsupported;
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallRouter.Error(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallRouter.Error("This market quantity or amount is too large.");
        }
    }

    private object? Account(PilotCallContext context, string method, object?[] args)
    {
        if (method == "GetEntryTypes" && args.Length == 0)
        {
            return new BlueList<WalletEntryTypeReply>(market.EntryTypes().Select(type => new WalletEntryTypeReply(type)).ToArray());
        }
        if (method == "GetCashBalance" && args is [] or [0L] or [false])
        {
            return Isk(market.Balance(context.Pilot.PilotId));
        }
        if (method != "GetTransactions" || args is not [(long)WalletAccountKey.Cash, var year, var month, false])
        {
            return PilotCallRouter.Unsupported;
        }
        // The default Last 30 Days filter passes (None, None); month filters
        // pass a real year and one-based month in build 3662168.
        bool recent = year is null && month is null;
        if (!recent && (year is not long y || y is < 1601 or > 9999 || month is not long m || m is < 1 or > 12))
        {
            return PilotCallRouter.Unsupported;
        }
        WalletJournalFilter filter = new(
            year is long yearNumber ? (int)yearNumber : null,
            month is long monthNumber ? (int)monthNumber : null
        );
        List<WalletJournalEntry> entries = [];
        foreach (var trade in market.Transactions(context.Pilot.PilotId))
        {
            if (!filter.Includes(trade.Time, clock.GetUtcNow().UtcDateTime))
            {
                continue;
            }
            long gross = checked(trade.PriceCents * trade.Quantity);
            if (trade.TaxCents > 0)
            {
                entries.Add(Journal(trade.Id * 2 + 1, trade, WalletEntryType.TransactionTax, -trade.TaxCents, trade.BalanceCents));
            }
            entries.Add(
                Journal(
                    trade.Id * 2,
                    trade,
                    WalletEntryType.MarketTransaction,
                    trade.IsBuy ? -gross : gross,
                    trade.BalanceCents + trade.TaxCents
                )
            );
        }
        foreach (var reward in combat.Rewards(context.Pilot.PilotId))
        {
            if (!filter.Includes(reward.Time, clock.GetUtcNow().UtcDateTime))
            {
                continue;
            }
            entries.Add(
                new(
                    CombatDefinitions.FirstBountyJournalId + reward.Id,
                    reward.Time,
                    WalletEntryType.BountyPrizes,
                    CombatDefinitions.ConcordCorporation,
                    reward.PilotId,
                    reward.SystemId,
                    reward.AmountCents,
                    reward.BalanceCents
                )
            );
        }
        foreach (var payment in repair.Payments(context.Pilot.PilotId))
        {
            if (!filter.Includes(payment.Time, clock.GetUtcNow().UtcDateTime))
            {
                continue;
            }
            entries.Add(
                new(
                    RepairDefinitions.FirstRepairJournalId + payment.Id,
                    payment.Time,
                    WalletEntryType.RepairBill,
                    payment.PilotId,
                    payment.StationOwnerId,
                    payment.StationId,
                    -payment.AmountCents,
                    payment.BalanceCents
                )
            );
        }
        return new BlueList<WalletJournalEntry>(entries.OrderByDescending(entry => entry.Time).ToArray());
    }

    private static WalletJournalEntry Journal(long id, MarketTrade trade, WalletEntryType type, long amount, long balance) =>
        new(
            id,
            trade.Time,
            type,
            type == WalletEntryType.TransactionTax || trade.IsBuy ? trade.PilotId : trade.CounterpartyId,
            type == WalletEntryType.TransactionTax ? MarketDefinitions.SccCorporationId
                : trade.IsBuy ? trade.CounterpartyId
                : trade.PilotId,
            trade.TypeId,
            amount,
            balance
        );
}
