using System.Buffers.Binary;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Trading;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Trading;

internal sealed record TradeMoniker(long ContainerId) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "carbon.common.script.net.moniker.Moniker",
            new object?[] { PlayerTradeHandler.Service, PilotLogin.NodeId, ContainerId, null }
        );
}

internal sealed record TradeAmounts(long Initiator, long Partner) : BlueModel
{
    public override object ToBlue() => new List<object?> { ToIsk(Initiator), ToIsk(Partner) };

    private static double ToIsk(long cents) => (double)cents / MarketDefinitions.CentsPerIsk;
}

internal sealed record TradeItems(IReadOnlyList<InventoryRow> Rows) : BlueModel
{
    public override object ToBlue() => new BlueConstructor("__builtin__.set", [Rows.Cast<object?>().ToList()]);
}

internal sealed record TradeManifest(long ContainerId, TradeAmounts Money, TradeItems Items) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["tradeContainerID"] = ContainerId,
                ["money"] = Money,
                ["tradeItems"] = Items,
            }
        );

    internal static long MoneyCents(object? value) =>
        value switch
        {
            long amount => PlayerTradeRules.MoneyCents(amount),
            double amount => PlayerTradeRules.MoneyCents(amount),
            _ => throw new InvalidDataException("Invalid trade amount."),
        };

    internal static TradeManifest Read(object? value)
    {
        if (
            value is not BlueInstance { Name: "utillib.KeyVal" or "util.KeyVal", State: Dictionary<string, object?> fields }
            || fields.GetValueOrDefault("tradeContainerID") is not long id
            || fields.GetValueOrDefault("money") is not List<object?> { Count: 2 } money
        )
        {
            throw new InvalidDataException("Invalid trade manifest.");
        }
        var rows = fields.GetValueOrDefault("tradeItems") switch
        {
            BlueRowSet set when set.Rows.Count <= PlayerTradeRules.MaximumItemsPerPilot * 2 => set.Rows.Select(ReadInventoryRow).ToArray(),
            HashSet<long> { Count: 0 } => [],
            _ => throw new InvalidDataException("Invalid trade item set."),
        };
        return new(id, new(MoneyCents(money[0]), MoneyCents(money[1])), new(rows));
    }

    private static InventoryRow ReadInventoryRow(BluePackedRow row)
    {
        if (!InventoryWire.IsInventoryDescriptor(row.Descriptor) || row.Objects is not [byte[] or string or null])
        {
            throw new InvalidDataException("Invalid trade inventory row.");
        }
        // Carbon lays out fixed-width values by decreasing width, preserving header order within each width.
        Span<byte> data = stackalloc byte[InventoryWire.PackedSize];
        row.Unpack(data);
        // customInfo can be None. All eight numeric columns must be present.
        if (data[38] != 0)
        {
            throw new InvalidDataException("Missing fields in the trade inventory row.");
        }
        return new(
            BinaryPrimitives.ReadInt64LittleEndian(data),
            BinaryPrimitives.ReadInt32LittleEndian(data[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[20..]),
            BinaryPrimitives.ReadInt64LittleEndian(data[8..]),
            BinaryPrimitives.ReadInt16LittleEndian(data[36..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[28..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[32..]),
            row.Objects[0] is null ? "" : BlueMarshal.Text(row.Objects[0])
        );
    }
}

internal sealed record TradeAcceptances(TradeManifest? Initiator, TradeManifest? Partner) : BlueModel
{
    public override object ToBlue() => new List<object?> { Initiator, Partner };
}

internal sealed record TradeList(
    long ContainerId,
    long Initiator,
    long Partner,
    TradeAcceptances State,
    TradeAmounts Money,
    TradeItems Items
) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["tradeContainerID"] = ContainerId,
                ["traders"] = new object?[] { Initiator, Partner },
                ["state"] = State,
                ["money"] = Money,
                ["items"] = Items,
            }
        );
}

internal sealed record TradeContainer(long ContainerId, long StationId, int StationType, long PilotId) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = ContainerId,
                ["typeID"] = StationType,
                ["ownerID"] = PilotId,
                ["locationID"] = StationId,
                ["flagID"] = (int)InventoryFlag.Hangar,
                ["quantity"] = InventoryWireDefinitions.AssembledQuantity,
                ["stacksize"] = 1,
                ["singleton"] = 1,
            }
        );
}

internal sealed record TradeInitiated(long PilotId, TradeMoniker Session) : PilotNotification("OnTradeInitiate")
{
    public override object?[] Arguments => [PilotId, Session];
}

internal sealed record TradeOfferChanged(long ContainerId, TradeAcceptances State) : PilotNotification("OnTradeOffer")
{
    public override object?[] Arguments => [ContainerId, State];
}

internal sealed record TradeOfferReset(long ContainerId) : PilotNotification("OnTradeOfferReset")
{
    public override object?[] Arguments => [ContainerId, true];
}

internal sealed record TradeMoneyChanged(long ContainerId, TradeAmounts Money) : PilotNotification("OnTradeMoneyOffer")
{
    public override object?[] Arguments => [ContainerId, Money];
}

internal sealed record TradeCancelled(long ContainerId) : PilotNotification("OnTradeCancel")
{
    public override object?[] Arguments => [ContainerId];
}

internal sealed record TradeCompleted(long ContainerId, TradeItems Items) : PilotNotification("OnTradeComplete")
{
    public override object?[] Arguments => [ContainerId, Items];
}

internal sealed record TradeLocationsNeeded(IReadOnlyList<long> ItemIds) : PilotNotification("OnPrimingNeededForTradeItems")
{
    public override object?[] Arguments => [ItemIds.Cast<object?>().ToList()];
}
