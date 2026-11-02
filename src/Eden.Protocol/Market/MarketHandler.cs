using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

[Injectable]
internal sealed class MarketHandler(IPilotMarket market, TimeProvider clock) : ISelectedPilotCallHandler
{
    private static readonly (string Name, BlueColumnType Type)[] s_columns =
    [
        ("orderID", BlueColumnType.Int64), ("typeID", BlueColumnType.Int32), ("charID", BlueColumnType.Int32),
        ("regionID", BlueColumnType.Int32), ("stationID", BlueColumnType.Int64), ("solarSystemID", BlueColumnType.Int32),
        ("range", BlueColumnType.Int16), ("bid", BlueColumnType.Boolean), ("price", BlueColumnType.Float64),
        ("volEntered", BlueColumnType.Int32), ("volRemaining", BlueColumnType.Int32), ("minVolume", BlueColumnType.Int32),
        ("issueDate", BlueColumnType.Int64), ("duration", BlueColumnType.Int16), ("jumps", BlueColumnType.Int32),
        ("escrow", BlueColumnType.Float64), ("isCorp", BlueColumnType.Boolean),
    ];

    private static double Isk(long cents) => (double)cents / MarketDefinitions.CentsPerIsk;
    private static BlueInstance KeyVal(Dictionary<string, object?> fields) => new("utillib.KeyVal", fields);
    private static BlueConstructor Descriptor((string Name, BlueColumnType Type)[] columns) => new("blue.DBRowDescriptor",
        [columns.Select(c => (object?)new object?[] { c.Name, (long)c.Type }).ToArray()]);
    private static BlueConstructor Row(MarketOrder order, int duration = MarketDefinitions.NpcOrderDays) => new("blue.DBRow",
        [Descriptor(s_columns), new object?[] { order.Id, order.TypeId, order.OwnerId, order.RegionId, order.StationId, order.SystemId,
            MarketDefinitions.StationRange, order.Bid, Isk(order.PriceCents), order.Entered, order.Remaining, 1, order.Issued, duration, 0, 0d, false }]);
    private static BlueConstructor Rows(IEnumerable<MarketOrder> orders) => new("carbon.common.script.sys.crowset.CRowset",
        [Descriptor(s_columns), orders.Select(o => (object?)Row(o)).ToList()]);

    private BlueInstance CachedOrders(object? result) => new("carbon.common.script.net.objectCaching.CachedMethodCallResult",
        new object?[] { new Dictionary<string, object?> { ["versionCheck"] = "always", ["sessionInfo"] = "regionid" },
            BlueMarshal.Encode(result), new object?[] { clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), 0L } });

    private static Dictionary<string, object?> Fields(object? value) => value is BlueInstance { Name: "utillib.KeyVal", State: Dictionary<string, object?> fields }
        ? fields : throw new InvalidDataException("Expected a market item request.");
    private static long Id(Dictionary<string, object?> fields, string key) => fields.GetValueOrDefault(key) is long id
        ? id : throw new InvalidDataException($"Invalid market {key}.");
    private static int Number(Dictionary<string, object?> fields, string key) => checked((int)Id(fields, key));
    private static long Price(object? price) => MarketDefinitions.PriceCents(price switch
    {
        double number => number,
        long number => number,
        _ => throw new InvalidDataException("Invalid market price."),
    });

    private List<object?> Changed(PilotCallContext context, MarketResult result)
    {
        foreach (var change in result.Changes) { InventoryHandler.NotifyItemChange(context, change); }
        context.Notifications.Add(("OnAccountChange", ["cash", context.Pilot.PilotId, Isk(result.BalanceCents)]));
        var orders = result.Trades.Select(trade => (object?)Row(new(MarketDefinitions.FirstExecutionOrderId + trade.Id,
            trade.StationId, context.Pilot.SystemId, context.Pilot.RegionId, context.Pilot.PilotId,
            trade.TypeId, trade.IsBuy, trade.PriceCents, 0, trade.Quantity, trade.Time), 0)).ToList();
        // Completed immediate orders invalidate the client's order and transaction
        // caches. They are executions, not outstanding personal listings.
        context.Notifications.Add(("OnOwnOrdersChanged", [orders, "Expiry", false]));
        foreach (var change in result.Changes.Where(c => c.Previous is null))
        {
            context.Notifications.Add(("OnMarketItemReceived", [change.Item.Id, change.Item.TypeId, change.Item.FlagId, change.Item.LocationId]));
        }
        return orders;
    }

    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        if (service == "account") { return Account(context, method, args); }
        if (service != "marketProxy") { return PilotCallRouter.Unsupported; }
        try
        {
            switch (method)
            {
                case "StartupCheck":
                    return null;
                case "GetOrders" when args is [long type] && type is > 0 and <= int.MaxValue:
                    var book = market.Orders(context.Pilot.PilotId).Where(o => o.TypeId == type).ToArray();
                    return CachedOrders(new object?[] { Rows(book.Where(o => !o.Bid)), Rows(book.Where(o => o.Bid)) });
                case "GetStationAsks":
                case "GetSystemAsks":
                case "GetRegionBest":
                    return market.Orders(context.Pilot.PilotId).Where(o => !o.Bid && (method == "GetRegionBest" ||
                        (method == "GetStationAsks" ? o.StationId == context.Pilot.StationId : o.SystemId == context.Pilot.SystemId)))
                        .GroupBy(o => o.TypeId).ToDictionary(g => (long)g.Key, g => (object?)Row(g.OrderBy(o => o.PriceCents).ThenBy(o => o.Id).First()));
                case "GetCharOrders":
                case "GetCorporationOrders":
                case "GetMarketOrderHistory":
                    return Rows([]);
                case "GetOldPriceHistory":
                case "GetNewPriceHistory":
                    return new BlueConstructor("carbon.common.script.sys.crowset.CRowset", [Descriptor([("historyDate", BlueColumnType.Int64),
                        ("lowPrice", BlueColumnType.Float64), ("highPrice", BlueColumnType.Float64), ("avgPrice", BlueColumnType.Float64),
                        ("volume", BlueColumnType.Int64), ("orders", BlueColumnType.Int32)]), new List<object?>()]);
                case "PlaceMultiSellOrder" when args is [List<object?> entries, false, 0L, _]:
                    var sales = entries.Select(Fields).Select(f => new MarketSale(Id(f, "itemID"), Number(f, "stationID"),
                        Number(f, "typeID"), Price(f.GetValueOrDefault("price")), Number(f, "quantity"))).ToArray();
                    Changed(context, market.Sell(context.Pilot.PilotId, sales));
                    return true;
                case "PlaceBuyOrder" when args is [long station, long buyType, var price, long quantity, (long)MarketDefinitions.StationRange, 1L, 0L, false, _]:
                    Changed(context, market.Buy(context.Pilot.PilotId, checked((int)station), [new(checked((int)buyType), Price(price), checked((int)quantity))]));
                    return null;
                case "BuyMultipleItems" when args is [long destination, List<object?> entries, false]:
                    var purchases = entries.Select(Fields).Select(f => new MarketPurchase(Number(f, "typeID"),
                        Price(f.GetValueOrDefault("price")), Number(f, "quantity"))).ToArray();
                    return Changed(context, market.Buy(context.Pilot.PilotId, checked((int)destination), purchases));
                case "CharGetTransactions" when args is [null or long]:
                    return market.Transactions(context.Pilot.PilotId).Where(t => args[0] is not long from || t.Time < from).Take(1000)
                        .Select(t => (object?)new Dictionary<string, object?>
                        {
                            ["transactionID"] = t.Id,
                            ["transactionDate"] = t.Time,
                            ["typeID"] = t.TypeId,
                            ["quantity"] = t.Quantity,
                            ["price"] = Isk(t.PriceCents),
                            ["stationID"] = t.StationId,
                            ["accountID"] = context.Pilot.PilotId,
                            ["buyerID"] = t.IsBuy ? t.PilotId : t.CounterpartyId,
                            ["sellerID"] = t.IsBuy ? t.CounterpartyId : t.PilotId,
                            ["buyerAccountID"] = t.IsBuy ? t.PilotId : t.CounterpartyId,
                            ["sellerAccountID"] = t.IsBuy ? t.CounterpartyId : t.PilotId,
                        }).ToList();
                case "PlaceMultiSellOrder":
                case "PlaceBuyOrder":
                case "BuyMultipleItems":
                    return new PilotCallRouter.Error("Eden currently supports immediate personal trades at your docked station.");
                default:
                    return PilotCallRouter.Unsupported;
            }
        }
        catch (InvalidDataException error) { return new PilotCallRouter.Error(error.Message); }
        catch (OverflowException) { return new PilotCallRouter.Error("This market quantity or amount is too large."); }
    }

    private object? Account(PilotCallContext context, string method, object?[] args)
    {
        if (method == "GetEntryTypes" && args.Length == 0)
        {
            return market.EntryTypes().Select(t => (object?)KeyVal(new()
            {
                ["entryTypeID"] = t.Id,
                ["entryTypeNameID"] = t.NameId,
                ["entryTypeName"] = t.Name,
                ["description"] = t.Description,
            })).ToList();
        }
        if (method == "GetCashBalance" && args is [] or [0L] or [false]) { return Isk(market.Balance(context.Pilot.PilotId)); }
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
        List<object?> entries = [];
        foreach (var trade in market.Transactions(context.Pilot.PilotId))
        {
            var date = DateTime.FromFileTimeUtc(trade.Time);
            if (recent ? date < clock.GetUtcNow().UtcDateTime.AddDays(-30) : date.Year != (long)year! || date.Month != (long)month!) { continue; }
            long gross = checked(trade.PriceCents * trade.Quantity);
            if (trade.TaxCents > 0) { entries.Add(Journal(trade.Id * 2 + 1, trade, WalletEntryType.TransactionTax, -trade.TaxCents, trade.BalanceCents)); }
            entries.Add(Journal(trade.Id * 2, trade, WalletEntryType.MarketTransaction, trade.IsBuy ? -gross : gross, trade.BalanceCents + trade.TaxCents));
        }
        return entries;
    }

    private static BlueInstance Journal(long id, MarketTrade trade, WalletEntryType type, long amount, long balance) => KeyVal(new()
    {
        ["transactionID"] = id,
        ["transactionDate"] = trade.Time,
        ["entryTypeID"] = (int)type,
        ["ownerID1"] = type == WalletEntryType.TransactionTax || trade.IsBuy ? trade.PilotId : trade.CounterpartyId,
        ["ownerID2"] = type == WalletEntryType.TransactionTax ? MarketDefinitions.SccCorporationId : trade.IsBuy ? trade.CounterpartyId : trade.PilotId,
        ["referenceID"] = trade.TypeId,
        ["amount"] = Isk(amount),
        ["balance"] = Isk(balance),
        ["description"] = null,
    });
}
