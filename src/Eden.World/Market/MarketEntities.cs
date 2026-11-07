namespace Eden.World.Market;

public sealed class PilotWallet
{
    public long PilotId { get; set; }
    public long Cents { get; set; }
}

public sealed class MarketStation
{
    public int StationId { get; set; }
    public long CreatedAt { get; set; }
    public int SeedVersion { get; set; }
}

public sealed class NpcMarketOrder
{
    public long Id { get; set; }
    public int StationId { get; set; }
    public int SystemId { get; set; }
    public int RegionId { get; set; }
    public long OwnerId { get; set; }
    public int TypeId { get; set; }
    public bool Bid { get; set; }
    public long PriceCents { get; set; }
    public int Remaining { get; set; }
    public int Entered { get; set; }
    public long Issued { get; set; }
    public Eden.Gameplay.Market.NpcMarketKind Kind { get; set; }
}

public sealed class MarketExecution
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long OrderId { get; set; }
    public int StationId { get; set; }
    public long CounterpartyId { get; set; }
    public int TypeId { get; set; }
    public bool IsBuy { get; set; }
    public int Quantity { get; set; }
    public long PriceCents { get; set; }
    public long TaxCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }
    public long? WalletAmountCents { get; set; }
}

public sealed class TestingWalletGrant
{
    public long PilotId { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }
}
