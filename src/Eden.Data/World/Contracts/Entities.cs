using Eden.Gameplay.Contracts;
using Eden.Gameplay.Market;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Contracts;

public sealed class SavedContract
{
    public long BuyoutCents { get; set; }
    public long CurrentBidCents { get; set; }
    public int AuctionAccountingLevel { get; set; }
    public bool IssuerCollected { get; set; }
    public bool WinnerCollected { get; set; }
    public ContractType Type { get; set; } = ContractType.ItemExchange;
    public int EndStationId { get; set; }
    public int EndSystemId { get; set; }
    public int EndConstellationId { get; set; }
    public int EndRegionId { get; set; }
    public int DurationDays { get; set; }
    public long CollateralCents { get; set; }
    public long? PackageId { get; set; }
    public long Id { get; set; }
    public long IssuerId { get; set; }
    public int IssuerCorporationId { get; set; }
    public long? AssigneeId { get; set; }
    public long? AcceptorId { get; set; }
    public ContractStatus Status { get; set; }
    public int StationId { get; set; }
    public int SystemId { get; set; }
    public int ConstellationId { get; set; }
    public int RegionId { get; set; }
    public long Issued { get; set; }
    public long Expires { get; set; }
    public long? Accepted { get; set; }
    public long? Completed { get; set; }
    public long PriceCents { get; set; }
    public long RewardCents { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public double Volume { get; set; }
    public long BrokerCents { get; set; }
    public long DepositCents { get; set; }
    public long SalesTaxCents { get; set; }
    public bool Reclaimed { get; set; }
    public bool Dismissed { get; set; }

    public PersonalContract Snapshot() =>
        new(
            Id,
            IssuerId,
            IssuerCorporationId,
            AssigneeId,
            AcceptorId,
            Status,
            StationId,
            SystemId,
            ConstellationId,
            RegionId,
            Issued,
            Expires,
            Accepted,
            Completed,
            PriceCents,
            RewardCents,
            Title,
            Description,
            Volume,
            new(BrokerCents, DepositCents, SalesTaxCents),
            Type,
            Type == ContractType.Courier
                ? new(new(EndStationId, EndSystemId, EndConstellationId, EndRegionId), DurationDays, CollateralCents, PackageId)
                : null,
            Type == ContractType.Auction ? new(BuyoutCents, CurrentBidCents, IssuerCollected, WinnerCollected) : null
        );
}

public sealed class SavedContractBid
{
    public long Id { get; set; }
    public long ContractId { get; set; }
    public long BidderId { get; set; }
    public long AmountCents { get; set; }
    public long Time { get; set; }
    public bool Dismissed { get; set; }

    public ContractBid Snapshot() => new(Id, BidderId, AmountCents, Time);
}

public sealed class SavedContractChange
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long ContractId { get; set; }
    public ContractChangeKind Kind { get; set; }
    public string InventoryJson { get; set; } = "[]";
    public long? BalanceCents { get; set; }
}

public sealed class SavedContractEscrow
{
    public long ItemId { get; set; }
    public long ContractId { get; set; }
}

public sealed class SavedContractItem
{
    public long Id { get; set; }
    public long ContractId { get; set; }
    public long ItemId { get; set; }
    public int TypeId { get; set; }
    public int Quantity { get; set; }
    public bool Offered { get; set; }
    public long? ParentId { get; set; }
    public int FlagId { get; set; }
    public bool Singleton { get; set; }
    public double Damage { get; set; }
    public int GroupId { get; set; }
    public int CategoryId { get; set; }

    public ContractItem Snapshot() => new(ItemId, TypeId, Quantity, Offered, ParentId, FlagId, Singleton, Damage, GroupId, CategoryId);
}

public sealed class SavedContractPayment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long CounterpartyId { get; set; }
    public long ContractId { get; set; }
    public WalletEntryType Type { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }

    public ContractPayment Snapshot() => new(Id, PilotId, CounterpartyId, ContractId, Type, AmountCents, BalanceCents, Time);
}
