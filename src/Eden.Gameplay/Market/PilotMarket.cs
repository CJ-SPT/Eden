using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Market;

public sealed record MarketOrder(
    long Id,
    int StationId,
    int SystemId,
    int RegionId,
    long OwnerId,
    int TypeId,
    bool Bid,
    long PriceCents,
    int Remaining,
    int Entered,
    long Issued,
    int DurationDays = MarketDefinitions.NpcOrderDays,
    long EscrowCents = 0
);

public sealed record MarketSale(long ItemId, int StationId, int TypeId, long LimitPriceCents, int Quantity);

public sealed record MarketPurchase(int TypeId, long LimitPriceCents, int Quantity);

public sealed record MarketTrade(
    long Id,
    long PilotId,
    long OrderId,
    int StationId,
    long CounterpartyId,
    int TypeId,
    bool IsBuy,
    int Quantity,
    long PriceCents,
    long TaxCents,
    long BalanceCents,
    long Time,
    long? WalletAmountCents = null
);

public sealed record MarketResult(long BalanceCents, IReadOnlyList<MarketTrade> Trades, IReadOnlyList<InventoryChange> Changes);

public sealed record WalletEntryDefinition(int Id, long NameId, string Name, string Description);

public sealed record MarketHistoryDay(
    int TypeId,
    long Date,
    long LowPriceCents,
    long HighPriceCents,
    decimal AveragePriceCents,
    long Volume,
    int Orders
);

public interface IPilotMarket
{
    void CheckStation(long pilotId, int stationId);
    long Balance(long pilotId);
    IReadOnlyList<MarketOrder> Orders(long pilotId, int? typeId = null);
    IReadOnlyList<MarketOrder> BestAsks(long pilotId, MarketScope scope);
    IReadOnlyList<MarketTrade> Transactions(long pilotId, MarketTransactionQuery? query = null);
    IReadOnlyList<WalletEntryDefinition> EntryTypes();
    IReadOnlyList<MarketHistoryDay> History(long pilotId, IReadOnlyList<int> typeIds);
    MarketResult Sell(long pilotId, IReadOnlyList<MarketSale> items);
    MarketResult Buy(long pilotId, int stationId, IReadOnlyList<MarketPurchase> items);
}

public enum MarketScope
{
    Station,
    System,
    Region,
}

public enum MarketSkill
{
    Accounting = 16622,
    Trade = 3443,
    Retail = 3444,
    Wholesale = 16596,
    Tycoon = 18580,
    BrokerRelations = 3446,
    AdvancedBrokerRelations = 16597,
}

public enum WalletEntryType
{
    MarketTransaction = 2,
    MarketEscrow = 42,
    BrokersFee = 46,
    GmCashTransfer = 3,
    PlayerDonation = 10,
    CorporationAccountWithdrawal = 37,
    BountyPrizeCorporationTax = 92,
    CorporationRegistration = 39,
    RepairBill = 15,
    Insurance = 19,
    CloneTransfer = 52,
    TransactionTax = 54,
    JumpCloneInstallation = 55,
    ContractRewardRefund = 66,
    ContractAuctionBid = 63,
    ContractAuctionBidRefund = 64,
    ContractCollateral = 65,
    ContractAuctionSold = 67,
    ContractReward = 68,
    ContractCollateralRefund = 69,
    ContractCollateralPayout = 70,
    ContractPrice = 71,
    ContractBrokersFee = 72,
    ContractSalesTax = 73,
    ContractDeposit = 74,
    ContractDepositSalesTax = 75,
    ContractDepositRefund = 82,
    ContractRewardAdded = 83,
    JumpCloneActivation = 128,
    BountyPrizes = 85,

    // Build 3662168 accountingentrytypes / appConst.refRedeemedISKToken.
    RedeemedIskToken = 147,
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
    // NPC orders carry a one-year issue period; testing asks renew on access.
    public const int StationRange = -1;
    public const int NpcOrderDays = 365;
    public const decimal SalesTaxRate = 0.075m;
    public const decimal AccountingReductionPerLevel = 0.11m;
    public const int CentsPerIsk = 100;
    public const decimal MaximumOrderPrice = 9_223_372_036_854m;
    public const long FirstExecutionOrderId = 5_000_000_000_000;
    public const int SccCorporationId = 1000132;
    public const int PlexTypeId = 44992;
    public const int HistoryDays = 365;

    public static long PriceCents(double price)
    {
        if (!double.IsFinite(price) || price <= 0 || price > (double)MaximumOrderPrice)
        {
            throw new InvalidDataException("Enter a positive market price within the client limit.");
        }
        decimal rounded = Math.Round((decimal)price * CentsPerIsk, 0, MidpointRounding.ToEven);
        if (rounded < 1)
        {
            throw new InvalidDataException("Market prices must be at least 0.01 ISK.");
        }
        return checked((long)rounded);
    }
}
