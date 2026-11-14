using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Trading;

public sealed record TradeAsset(InventoryItem Root, IReadOnlyList<InventoryItem> Contents, string? ShipName);

public sealed record TradeItemOffer(TradeAsset Asset, int Quantity);

public sealed record TradeSide(long PilotId, long MoneyCents, IReadOnlyList<TradeItemOffer> Items);

public sealed record TradeSettlement(IReadOnlyList<InventoryChange> InitiatorChanges, IReadOnlyList<InventoryChange> PartnerChanges);

public interface IPlayerTradeStore
{
    long Balance(long pilotId);
    IReadOnlyList<TradeItemOffer> Quote(long pilotId, long stationId, InventoryMoveRequest request);
    TradeSettlement Settle(long stationId, TradeSide initiator, TradeSide partner);
}

public static class PlayerTradeRules
{
    public const int MaximumItemsPerPilot = 250;

    public static long MoneyCents(double isk)
    {
        if (!double.IsFinite(isk) || isk < 0 || isk > (double)MarketDefinitions.MaximumOrderPrice)
        {
            throw new InvalidDataException("Enter a valid trade amount in ISK.");
        }
        return decimal.ToInt64(decimal.Round((decimal)isk * MarketDefinitions.CentsPerIsk, 0, MidpointRounding.AwayFromZero));
    }

    public static int Quantity(TradeAsset asset, int? requested)
    {
        var root = asset.Root;
        int quantity = requested ?? root.Quantity;
        if (quantity <= 0 || quantity > root.Quantity || (root.Singleton || asset.Contents.Count > 0) && quantity != root.Quantity)
        {
            throw new InvalidDataException("Choose an available quantity or offer the whole assembled item.");
        }
        return quantity;
    }

    public static long BalanceAfterTrade(long balance, long offered, long received)
    {
        if (offered < 0 || received < 0 || balance < offered)
        {
            throw new InvalidDataException("A pilot no longer has enough ISK to complete the trade.");
        }
        if (received > long.MaxValue - (balance - offered))
        {
            throw new InvalidDataException("The trade would exceed a pilot's wallet limit.");
        }
        return balance - offered + received;
    }
}
