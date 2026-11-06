using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Market;

public enum PersonalOrderStatus
{
    Open,
    Filled,
    Cancelled,
    Expired,
}

public sealed record PersonalMarketBuy(int StationId, int TypeId, long PriceCents, int Quantity, int DurationDays);

public sealed record PersonalMarketModification(
    long OrderId,
    long PriceCents,
    bool Bid,
    int StationId,
    int SystemId,
    long PreviousPriceCents,
    int Remaining,
    long Issued
);

public sealed record MarketEscrow(long IskCents, long ItemsValueCents);

public sealed record MarketMatch(int Quantity, long PriceCents, long GrossCents, long ReservedCents, long RefundCents);

public sealed record MarketPayment(
    long Id,
    long PilotId,
    long CounterpartyId,
    long OrderId,
    WalletEntryType Kind,
    long AmountCents,
    long BalanceCents,
    long Time
);

public enum MarketOrderChangeKind
{
    Placed,
    Modified,
    Settled,
}

public sealed record PersonalMarketChange(long Id, MarketOrder Order, MarketOrderChangeKind Kind, IReadOnlyList<InventoryChange> Inventory);

public sealed record WorldEconomySettings(
    bool TestingMarket = true,
    bool TestingGrants = true,
    int InstitutionalStock = NpcSupplyRules.Stock
)
{
    public const int SingletonId = 1;
}

public interface IPersonalMarket
{
    long Revision { get; }
    long NextExpiry { get; }
    long Observe(long pilotId);
    IReadOnlyList<PersonalMarketChange> Changes(long pilotId, long after);
    IReadOnlyList<MarketOrder> PersonalOrders(long pilotId, bool history = false);
    MarketEscrow Escrow(long pilotId);
    void PlaceBuy(long pilotId, PersonalMarketBuy request);
    void PlaceSell(long pilotId, IReadOnlyList<MarketSale> items, int durationDays);
    void Modify(long pilotId, PersonalMarketModification request);
    void Cancel(long pilotId, long orderId, int regionId);
    int Expire(int limit = PersonalMarketRules.MaintenanceBatch);
    WorldEconomySettings Economy();
}

public static class PersonalMarketRules
{
    // Disjoint from NPC orders and the protocol's synthetic immediate-order IDs.
    public const long FirstOrderId = 6_000_000_000_000;
    public const long FirstPaymentJournalId = 18_000_000_000_000;
    public const int MaintenanceBatch = 100;
    public const int ChangeBatch = 100;
    public const int ModificationSeconds = 300;
    public const long MinimumBrokerFeeCents = 100 * MarketDefinitions.CentsPerIsk;

    public static bool Crosses(bool bid, long offer, long resting) => bid ? resting <= offer : resting >= offer;

    public static MarketMatch Match(bool bid, long offer, int requested, long resting, int available, bool npc)
    {
        if (!Crosses(bid, offer, resting))
        {
            throw new InvalidDataException("These market orders do not cross.");
        }
        // Personal orders execute at the incoming offer. Development NPC orders
        // retain the existing advertised-price behavior.
        long price = npc ? resting : offer;
        var fill = MarketRules.Fill(requested, available, price);
        long reserved = checked((bid ? offer : resting) * fill.Quantity);
        return new(fill.Quantity, price, fill.GrossCents, reserved, reserved - fill.GrossCents);
    }

    // Pinned marketutil.skilllimits.GetSkillLimits.
    public static int OrderLimit(int trade, int retail, int wholesale, int tycoon) =>
        5 + 4 * trade + 8 * retail + 16 * wholesale + 32 * tycoon;

    // CCP NPC-station broker formula and relist discount; server-side values
    // unavailable from catalog are pinned here rather than trusted client quotes.
    public static decimal BrokerRate(int level, decimal corporationStanding, decimal factionStanding) =>
        Math.Max(0.01m, 0.03m - 0.003m * level - 0.0002m * corporationStanding - 0.0003m * factionStanding);

    public static long BrokerFee(long price, int quantity, decimal rate) =>
        Math.Max(MinimumBrokerFeeCents, Round(checked(price * quantity) * rate));

    public static long RelistFee(long previous, long price, int quantity, decimal rate, int advancedLevel) =>
        Math.Max(
            MinimumBrokerFeeCents,
            Round(quantity * (Math.Max(0, rate * (price - previous)) + (1 - (0.5m + 0.06m * advancedLevel)) * rate * price))
        );

    public static long Round(decimal amount) => checked((long)Math.Round(amount, 0, MidpointRounding.ToEven));

    public static void Validate(int quantity, long price, int duration)
    {
        if (quantity <= 0 || price <= 0 || price > MarketDefinitions.MaximumOrderPrice * MarketDefinitions.CentsPerIsk)
        {
            throw new InvalidDataException("Choose a positive quantity and valid market price.");
        }
        if (duration is not (0 or 1 or 3 or 7 or 14 or 30 or 90))
        {
            throw new InvalidDataException("Choose a supported market order duration.");
        }
        _ = checked(price * quantity);
    }
}
