using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Repair;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

[Injectable]
internal sealed class MarketHandler(
    IPilotMarket market,
    TimeProvider clock,
    ICorporationWallet corporationWallet,
    IPersonalMarket personal,
    PersonalMarketMonitor monitor
) : ISelectedPilotCallHandler
{
    private static double Isk(long cents) => (double)cents / MarketDefinitions.CentsPerIsk;

    private static MarketOrderRow Row(MarketOrder order, int? duration = null) => new(order, duration);

    private static MarketOrderRows Rows(IEnumerable<MarketOrder> orders) => new(orders.ToArray());

    private MarketHistoryPair HistoryPair(IEnumerable<MarketHistoryDay> days) =>
        new(days.ToArray(), clock.GetUtcNow().UtcDateTime.Date.ToFileTimeUtc());

    private CachedMarketOrders CachedOrders(IBlueWritable result) => new(result, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());

    private CachedMarketOrders Book(long pilotId, int typeId)
    {
        var book = market.Orders(pilotId, typeId);
        return CachedOrders(new MarketOrderBook(book.Where(o => !o.Bid).ToArray(), book.Where(o => o.Bid).ToArray()));
    }

    private BlueList<MarketOrderRow> Changed(PilotCallContext context, MarketResult result)
    {
        foreach (var change in result.Changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
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

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool check = service == "corpStationMgr" && method == "DoStandingCheckForStationService" && args is [(long)StationService.Market];
        if (service != "marketProxy" && !check)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unsupported market keyword arguments.");
            }
            monitor.Enter(context);
            if (check)
            {
                market.CheckStation(context.Pilot.PilotId, context.Pilot.StationId);
                return PilotCallResult.None;
            }
            switch (method)
            {
                case "StartupCheck":
                    return PilotCallResult.None;
                case "GetOrders" when args is [long type] && type is > 0 and <= int.MaxValue:
                    return Book(context.Pilot.PilotId, (int)type);
                case "GetPlexOrders" when args.Length == 0:
                    return Book(context.Pilot.PilotId, MarketDefinitions.PlexTypeId);
                case "GetPlexBest" when args.Length == 0:
                    return new BlueMap<long, MarketOrderRow>(
                        market
                            .Orders(context.Pilot.PilotId, MarketDefinitions.PlexTypeId)
                            .Where(o => !o.Bid)
                            .OrderBy(o => o.PriceCents)
                            .ThenBy(o => o.Id)
                            .Take(1)
                            .ToDictionary(o => (long)o.TypeId, o => Row(o))
                    );
                case "GetStationAsks" or "GetSystemAsks" or "GetRegionBest" when args.Length == 0:
                    var scope = method switch
                    {
                        "GetStationAsks" => MarketScope.Station,
                        "GetSystemAsks" => MarketScope.System,
                        _ => MarketScope.Region,
                    };
                    return new BlueMap<long, MarketOrderRow>(
                        market.BestAsks(context.Pilot.PilotId, scope).ToDictionary(o => (long)o.TypeId, o => Row(o))
                    );
                case "GetCharOrders" when args.Length == 0:
                    return Rows(personal.PersonalOrders(context.Pilot.PilotId));
                case "GetMarketOrderHistory" when args.Length == 0:
                    return Rows(personal.PersonalOrders(context.Pilot.PilotId, history: true));
                case "GetCharEscrow" when args.Length == 0:
                    var escrow = personal.Escrow(context.Pilot.PilotId);
                    return new EscrowInfo(Isk(escrow.IskCents), Isk(escrow.ItemsValueCents));
                case "GetCorporationOrders":
                    return Rows([]);
                case "CancelCharOrder" when args is [long cancelId, long cancelRegion]:
                    personal.Cancel(context.Pilot.PilotId, cancelId, checked((int)cancelRegion));
                    monitor.Advance(context);
                    return PilotCallResult.None;
                case "ModifyCharOrder"
                    when args
                        is [
                            long modifyId,
                            var newPrice,
                            bool bid,
                            long modifyStation,
                            long modifySystem,
                            var oldPrice,
                            (long)MarketDefinitions.StationRange,
                            long remaining,
                            long issued,
                        ]:
                    personal.Modify(
                        context.Pilot.PilotId,
                        new(
                            modifyId,
                            MarketRequestWire.Price(newPrice),
                            bid,
                            checked((int)modifyStation),
                            checked((int)modifySystem),
                            MarketRequestWire.Price(oldPrice),
                            checked((int)remaining),
                            issued
                        )
                    );
                    monitor.Advance(context);
                    return PilotCallResult.None;
                case "GetOldPriceHistory" or "GetNewPriceHistory" when args is [long type] && type is > 0 and <= int.MaxValue:
                    return HistoryPair(market.History(context.Pilot.PilotId, [(int)type])).For(method == "GetNewPriceHistory");
                case "GetHistoryForManyTypeIDs" when args is [HashSet<long> types] && types.All(t => t is > 0 and <= int.MaxValue):
                    var days = market.History(context.Pilot.PilotId, types.Select(t => (int)t).ToArray());
                    return new BlueMap<long, MarketHistoryPair>(
                        types.ToDictionary(t => t, t => HistoryPair(days.Where(d => d.TypeId == t)))
                    );
                case "GetPlexHistory" when args.Length == 0:
                    return new BlueMap<long, MarketHistoryPair>(
                        new Dictionary<long, MarketHistoryPair>
                        {
                            [MarketDefinitions.PlexTypeId] = HistoryPair(
                                market.History(context.Pilot.PilotId, [MarketDefinitions.PlexTypeId])
                            ),
                        }
                    );
                // The pinned corporation-wallet checkbox returns Python int 0/1;
                // windows without that checkbox send a Python bool instead.
                case "PlaceMultiSellOrder" when args is [List<object?> listedItems, false or 0L, long sellDays, _] && sellDays > 0:
                    personal.PlaceSell(context.Pilot.PilotId, listedItems.Select(MarketRequestWire.Sale).ToArray(), checked((int)sellDays));
                    monitor.Advance(context);
                    return true;
                case "PlaceBuyOrder"
                    when args
                        is [
                            long listedStation,
                            long listedType,
                            var listedPrice,
                            long listedQuantity,
                            (long)MarketDefinitions.StationRange,
                            1L,
                            long buyDays,
                            false
                            or 0L,
                            _,
                        ]
                        && buyDays > 0:
                    personal.PlaceBuy(
                        context.Pilot.PilotId,
                        new(
                            checked((int)listedStation),
                            checked((int)listedType),
                            MarketRequestWire.Price(listedPrice),
                            checked((int)listedQuantity),
                            checked((int)buyDays)
                        )
                    );
                    monitor.Advance(context);
                    return PilotCallResult.None;
                case "PlaceMultiSellOrder" when args is [List<object?> entries, false or 0L, 0L, _]:
                    var sales = entries.Select(MarketRequestWire.Sale).ToArray();
                    Changed(context, market.Sell(context.Pilot.PilotId, sales));
                    return true;
                case "PlaceBuyOrder"
                    when args
                        is [
                            long station,
                            long buyType,
                            var price,
                            long quantity,
                            (long)MarketDefinitions.StationRange,
                            1L,
                            0L,
                            false
                            or 0L,
                            _,
                        ]:
                    Changed(
                        context,
                        market.Buy(
                            context.Pilot.PilotId,
                            checked((int)station),
                            [new(checked((int)buyType), MarketRequestWire.Price(price), checked((int)quantity))]
                        )
                    );
                    return PilotCallResult.None;
                case "BuyMultipleItems" when args is [long destination, List<object?> entries, false or 0L]:
                    var purchases = entries.Select(MarketRequestWire.Purchase).ToArray();
                    return Changed(context, market.Buy(context.Pilot.PilotId, checked((int)destination), purchases));
                case "CharGetTransactions" when args is [null or long]:
                    return new BlueList<MarketTransactionReply>(
                        market
                            .Transactions(context.Pilot.PilotId, new(args[0] as long?))
                            .Select(trade => new MarketTransactionReply(trade, context.Pilot.PilotId))
                            .ToArray()
                    );
                case "CorpGetTransactions":
                    if (
                        args is not [null or long, null or long]
                        || keywords is { Count: > 0 }
                        || args[0] is long date && date < 0
                        || args[1] is long divisionKey
                            && divisionKey is < CorporationWalletRules.MasterKey or > CorporationWalletRules.LastKey
                    )
                    {
                        throw new InvalidDataException("Choose a valid corporate market date and wallet division.");
                    }
                    corporationWallet.CheckMarketHistoryAccess(context.Pilot.PilotId, args[1] is long selected ? (int)selected : null);
                    // All existing Eden executions are explicitly personal. Never expose
                    // a member's personal trade history as corporation transactions.
                    return BlueEmpty.List;
                case "PlaceMultiSellOrder":
                case "PlaceBuyOrder":
                case "BuyMultipleItems":
                case "ModifyCharOrder":
                case "CancelCharOrder":
                    return new PilotCallResult.Failure("Choose personal station orders, station range and a minimum volume of one.");
                default:
                    return PilotCallResult.Unsupported;
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("This market quantity or amount is too large.");
        }
    }
}
