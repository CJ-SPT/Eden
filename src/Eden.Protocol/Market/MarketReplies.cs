using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

internal sealed record WalletJournalFilter(int? Year, int? Month)
{
    public HistoryPeriod Period(DateTime utcNow)
    {
        if (Year is null || Month is null)
        {
            return new(utcNow.AddDays(-30).ToFileTimeUtc());
        }
        DateTime start = new(Year.Value, Month.Value, 1, 0, 0, 0, DateTimeKind.Utc);
        return new(start.ToFileTimeUtc(), Year == 9999 && Month == 12 ? null : start.AddMonths(1).ToFileTimeUtc());
    }

    public bool Includes(long time, DateTime utcNow)
    {
        var date = DateTime.FromFileTimeUtc(time);
        return Year is null && Month is null ? date >= utcNow.AddDays(-30) : date.Year == Year && date.Month == Month;
    }
}

internal sealed record MarketOrderRow(MarketOrder Order, int? Duration = null) : BlueModel
{
    public override object ToBlue() => MarketWire.Row(Order, Duration);
}

internal sealed record MarketBestAsks : BlueModel
{
    private static readonly ConditionalWeakTable<IReadOnlyList<MarketOrder>, MarketBestAsks> s_replies = new();
    private readonly IReadOnlyList<MarketOrder> _orders;
    private readonly Lazy<byte[]> _encoded;

    private MarketBestAsks(IReadOnlyList<MarketOrder> orders)
    {
        _orders = orders;
        _encoded = new(() => BlueMarshal.Encode(this));
    }

    internal static MarketBestAsks Read(IReadOnlyList<MarketOrder> orders) => s_replies.GetValue(orders, static orders => new(orders));

    public override object ToBlue() => _orders.ToDictionary(order => (long)order.TypeId, order => (object?)new MarketOrderRow(order));

    internal override byte[] EncodeReply() => _encoded.Value;
}

internal sealed record MarketOrderRows(IReadOnlyList<MarketOrder> Orders) : BlueModel
{
    public override object ToBlue() => MarketWire.Rows(Orders);
}

internal sealed record MarketOrderBook(IReadOnlyList<MarketOrder> Sell, IReadOnlyList<MarketOrder> Buy) : BlueModel
{
    public override object ToBlue() => new object?[] { new MarketOrderRows(Sell), new MarketOrderRows(Buy) };
}

internal sealed record MarketHistoryRows(IReadOnlyList<MarketHistoryDay> Days) : BlueModel
{
    public override object ToBlue() => MarketWire.HistoryRows(Days);
}

internal sealed record MarketHistoryPair(IReadOnlyList<MarketHistoryDay> Days, long Midnight) : BlueModel
{
    public MarketHistoryRows For(bool current) => new(Days.Where(day => current ? day.Date >= Midnight : day.Date < Midnight).ToArray());

    public override object ToBlue() => new object?[] { For(false), For(true) };
}

internal sealed record CachedMarketOrders(IBlueWritable Result, long Version) : BlueModel
{
    public override object ToBlue()
    {
        byte[] payload = BlueMarshal.Encode(Result);
        return new BlueInstance(
            "carbon.common.script.net.objectCaching.CachedMethodCallResult",
            new object?[]
            {
                new Dictionary<string, object?> { ["versionCheck"] = "always", ["sessionInfo"] = "regionid" },
                payload,
                new object?[] { Version, CachedMethodPayload.Checksum(payload) },
            }
        );
    }
}

internal sealed record MarketTransactionReply(MarketTrade Trade, long AccountId) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["transactionID"] = Trade.Id,
            ["transactionDate"] = Trade.Time,
            ["typeID"] = Trade.TypeId,
            ["quantity"] = Trade.Quantity,
            ["price"] = (double)Trade.PriceCents / MarketDefinitions.CentsPerIsk,
            ["stationID"] = Trade.StationId,
            ["accountID"] = AccountId,
            ["buyerID"] = Trade.IsBuy ? Trade.PilotId : Trade.CounterpartyId,
            ["sellerID"] = Trade.IsBuy ? Trade.CounterpartyId : Trade.PilotId,
            ["buyerAccountID"] = Trade.IsBuy ? Trade.PilotId : Trade.CounterpartyId,
            ["sellerAccountID"] = Trade.IsBuy ? Trade.CounterpartyId : Trade.PilotId,
        };
}

internal sealed record CorporationMarketTransactionReply(MarketTrade Trade) : BlueModel
{
    // The client compares accountID with buyerAccountID. A seller's receipt does not expose the buyer's wallet.
    private const long UnknownBuyerAccount = 0;

    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["transactionID"] = Trade.Id,
            ["transactionDate"] = Trade.Time,
            ["typeID"] = Trade.TypeId,
            ["quantity"] = Trade.Quantity,
            ["price"] = (double)Trade.PriceCents / MarketDefinitions.CentsPerIsk,
            ["stationID"] = Trade.StationId,
            ["locationID"] = Trade.StationId,
            ["accountID"] = Trade.WalletDivisionKey,
            ["keyID"] = Trade.WalletDivisionKey,
            ["buyerID"] = Trade.IsBuy ? Trade.PilotId : Trade.CounterpartyId,
            ["sellerID"] = Trade.IsBuy ? Trade.CounterpartyId : Trade.PilotId,
            ["buyerAccountID"] = Trade.IsBuy ? (long?)Trade.WalletDivisionKey : UnknownBuyerAccount,
            ["sellerAccountID"] = Trade.IsBuy ? Eden.Gameplay.Corporations.CorporationWalletRules.MasterKey : Trade.WalletDivisionKey,
        };
}

internal sealed record WalletEntryTypeReply(WalletEntryDefinition Type) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["entryTypeID"] = Type.Id,
                ["entryTypeNameID"] = Type.NameId,
                ["entryTypeName"] = Type.Name,
                ["description"] = Type.Description,
            }
        );
}

internal sealed record WalletJournalEntry(
    long Id,
    long Time,
    WalletEntryType Type,
    long OwnerId1,
    long OwnerId2,
    long ReferenceId,
    long AmountCents,
    long BalanceCents,
    string? Description = null
) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["transactionID"] = Id,
                ["transactionDate"] = Time,
                ["entryTypeID"] = (int)Type,
                ["ownerID1"] = OwnerId1,
                ["ownerID2"] = OwnerId2,
                ["referenceID"] = ReferenceId,
                ["amount"] = (double)AmountCents / MarketDefinitions.CentsPerIsk,
                ["balance"] = (double)BalanceCents / MarketDefinitions.CentsPerIsk,
                // Native transactionsUtil reads safe YAML metadata. JSON is a
                // compatible subset; escaping DESC keeps player text inert.
                ["description"] = string.IsNullOrEmpty(Description)
                    ? null
                    : new BlueUnicode(JsonSerializer.Serialize(new WalletDescription(Description))),
            }
        );
}

internal sealed record WalletDescription([property: JsonPropertyName("DESC")] string Reason);

internal sealed record OwnOrdersChanged(IReadOnlyList<MarketOrderRow> Orders, string Reason, bool IsCorporation = false)
    : PilotNotification("OnOwnOrdersChanged")
{
    public override object?[] Arguments => [Orders.Select(order => (object?)order.ToBlue()).ToList(), Reason, IsCorporation];
}

internal sealed record MarketItemReceived(InventoryItem Item) : PilotNotification("OnMarketItemReceived")
{
    public override object?[] Arguments => [Item.Id, Item.TypeId, Item.FlagId, Item.LocationId];
}
