using Eden.Protocol.Docking;

namespace Eden.Protocol.Market;

public sealed record MarketOrder(long Id, int StationId, int SystemId, int RegionId, long OwnerId,
    int TypeId, bool Bid, long PriceCents, int Remaining, int Entered, long Issued);
public sealed record MarketSale(long ItemId, int StationId, int TypeId, long LimitPriceCents, int Quantity);
public sealed record MarketPurchase(int TypeId, long LimitPriceCents, int Quantity);
public sealed record MarketTrade(long Id, long PilotId, long OrderId, int StationId, long CounterpartyId,
    int TypeId, bool IsBuy, int Quantity, long PriceCents, long TaxCents, long BalanceCents, long Time);
public sealed record MarketResult(long BalanceCents, IReadOnlyList<MarketTrade> Trades, IReadOnlyList<InventoryChange> Changes);
public sealed record WalletEntryDefinition(int Id, long NameId, string Name, string Description);

public interface IPilotMarket
{
    long Balance(long pilotId);
    IReadOnlyList<MarketOrder> Orders(long pilotId);
    IReadOnlyList<MarketTrade> Transactions(long pilotId);
    IReadOnlyList<WalletEntryDefinition> EntryTypes();
    MarketResult Sell(long pilotId, IReadOnlyList<MarketSale> items);
    MarketResult Buy(long pilotId, int stationId, IReadOnlyList<MarketPurchase> items);
}

public enum MarketSkill
{
    Accounting = 16622,
}

public enum WalletEntryType
{
    MarketTransaction = 2,
    TransactionTax = 54,
}

public enum WalletAccountKey
{
    Cash = 1000,
}

public static class MarketDefinitions
{
    // Pinned marketutil.const: station range and Accounting's reduction. Live
    // build 3662168 evaluates market-transaction-tax to 7.5%, overriding its 8%
    // packaged fallback. Pin that observed rate until local flag hosting exists.
    // Eden gives its finite starter NPC orders a one-year lifetime.
    public const int StationRange = -1;
    public const int NpcOrderDays = 365;
    public const decimal SalesTaxRate = 0.075m;
    public const decimal AccountingReductionPerLevel = 0.11m;
    public const int CentsPerIsk = 100;
    public const decimal MaximumOrderPrice = 9_223_372_036_854m;
    public const long FirstExecutionOrderId = 5_000_000_000_000;
    public const int SccCorporationId = 1000132;

    public static long PriceCents(double price)
    {
        if (!double.IsFinite(price) || price <= 0 || price > (double)MaximumOrderPrice)
        {
            throw new InvalidDataException("Enter a positive market price within the client limit.");
        }
        decimal rounded = Math.Round((decimal)price * CentsPerIsk, 0, MidpointRounding.ToEven);
        if (rounded < 1) { throw new InvalidDataException("Market prices must be at least 0.01 ISK."); }
        return checked((long)rounded);
    }
}
