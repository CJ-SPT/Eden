using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Market;

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

public sealed class MarketStation
{
    public int StationId { get; set; }
    public long CreatedAt { get; set; }
    public int SeedVersion { get; set; }
    public long StandardSnapshotTicks { get; set; }
}

[PrimaryKey(nameof(RegionId), nameof(TypeId))]
public sealed class SavedStandardMarketType
{
    public int RegionId { get; set; }
    public int TypeId { get; set; }
    public long SnapshotTicks { get; set; }
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
    public long SourceOrderId { get; set; }
    public int DurationDays { get; set; } = MarketDefinitions.NpcOrderDays;
    public int Range { get; set; } = MarketDefinitions.StationRange;
    public int MinimumVolume { get; set; } = 1;
}

public sealed class PilotWallet
{
    public long PilotId { get; set; }
    public long Cents { get; set; }
}

[PrimaryKey(nameof(PilotId), nameof(IssuerCorporationId))]
public sealed class SavedLoyaltyBalance
{
    public long PilotId { get; set; }
    public int IssuerCorporationId { get; set; }
    public long Points { get; set; }
}

public sealed class SavedMarketChange
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public string Payload { get; set; } = "";
}

public sealed class SavedMarketPayment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long CounterpartyId { get; set; }
    public long OrderId { get; set; }
    public WalletEntryType Kind { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }

    public MarketPayment Snapshot() => new(Id, PilotId, CounterpartyId, OrderId, Kind, AmountCents, BalanceCents, Time);
}

public sealed class SavedPersonalOrder
{
    public int? CorporationId { get; set; }
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int StationId { get; set; }
    public int SystemId { get; set; }
    public int RegionId { get; set; }
    public int TypeId { get; set; }
    public bool Bid { get; set; }
    public long PriceCents { get; set; }
    public int Remaining { get; set; }
    public int Entered { get; set; }
    public int MinimumVolume { get; set; } = 1;
    public int Range { get; set; } = MarketDefinitions.StationRange;
    public long Issued { get; set; }
    public long Expires { get; set; }
    public long Modified { get; set; }
    public int DurationDays { get; set; }
    public long EscrowCents { get; set; }
    public PersonalOrderStatus Status { get; set; }

    public MarketOrder Snapshot() =>
        new(
            Id,
            StationId,
            SystemId,
            RegionId,
            CorporationId ?? PilotId,
            TypeId,
            Bid,
            PriceCents,
            Remaining,
            Entered,
            Issued,
            DurationDays,
            EscrowCents,
            Range,
            MinimumVolume: MinimumVolume,
            IsCorporation: CorporationId.HasValue
        );
}

public sealed class SavedRedeemedCurrency
{
    public long TokenId { get; set; }
    public long PilotId { get; set; }
    public RedeemCurrencyKind Kind { get; set; }
    public long Amount { get; set; }
    public long Before { get; set; }
    public long After { get; set; }
    public long Time { get; set; }

    public RedeemedCurrency Snapshot() => new(TokenId, PilotId, Kind, Amount, Before, After, Time);
}

public sealed class SavedWorldEconomy
{
    public int Id { get; set; }
    public bool TestingMarket { get; set; } = true;
    public bool TestingGrants { get; set; } = true;
    public int InstitutionalStock { get; set; } = NpcSupplyRules.Stock;
}

public sealed class TestingWalletGrant
{
    public long PilotId { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }
}
