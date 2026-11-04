using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Contracts;

// Build 3662168 appConst contract type and status values.
public enum ContractType
{
    Nothing = 0,
    ItemExchange = 1,
    Auction = 2,
    Courier = 3,
    Loan = 4,
    AuctionAndItemExchange = 10,
    All = 11,
}

public enum ContractStatus
{
    // These two values are native list filters, never saved lifecycle states.
    RequiresAttention = -1,
    BidOnBy = 10,
    Outstanding = 0,
    InProgress = 1,
    FinishedIssuer = 2,
    FinishedContractor = 3,
    Finished = 4,
    Cancelled = 5,
    Rejected = 6,
    Failed = 7,
    Deleted = 8,
}

public enum ContractSkill
{
    Contracting = 25235,
    AdvancedContracting = 73912,
    BrokerRelations = 3446,
    Accounting = 16622,
}

public enum ContractChangeKind
{
    Created,
    Assigned,
    Accepted,
    Rejected,
    Reclaimed,
    Completed,
    Failed,
    CourierAccepted,
    AuctionBid,
    AuctionOutbid,
    AuctionBuyout,
}

public enum ContractRequestGroup
{
    General = 280,
    Frozen = 281,
    Livestock = 283,
    Miscellaneous = 314,
    Commodities = 526,
}

public sealed record ContractOffer(long ItemId, int Quantity);

public sealed record ContractRequest(int TypeId, int Quantity);

public sealed record ContractDraft(
    long? AssigneeId,
    int StationId,
    int ExpiryMinutes,
    long PriceCents,
    long RewardCents,
    string Title,
    string Description,
    IReadOnlyList<ContractOffer> Items,
    IReadOnlyList<ContractRequest> RequestedItems,
    ContractType Type = ContractType.ItemExchange,
    CourierTerms? Courier = null,
    AuctionTerms? Auction = null,
    bool MultiContract = false
);

public sealed record ContractFees(long BrokerCents, long DepositCents, long SalesTaxCents);

public sealed record ContractSkills(int Contracting, int AdvancedContracting, int BrokerRelations, int Accounting);

public sealed record PersonalContract(
    long Id,
    long IssuerId,
    int IssuerCorporationId,
    long? AssigneeId,
    long? AcceptorId,
    ContractStatus Status,
    int StationId,
    int SystemId,
    int ConstellationId,
    int RegionId,
    long Issued,
    long Expires,
    long? Accepted,
    long? Completed,
    long PriceCents,
    long RewardCents,
    string Title,
    string Description,
    double Volume,
    ContractFees Fees,
    ContractType Type = ContractType.ItemExchange,
    CourierContract? Courier = null,
    AuctionState? Auction = null
);

public sealed record ContractItem(
    long ItemId,
    int TypeId,
    int Quantity,
    bool Offered,
    long? ParentId,
    int FlagId,
    bool Singleton,
    double Damage,
    int GroupId,
    int CategoryId
);

public sealed record ContractDetails(PersonalContract Contract, IReadOnlyList<ContractItem> Items, IReadOnlyList<ContractBid>? Bids = null);

public sealed record ContractPayment(
    long Id,
    long PilotId,
    long CounterpartyId,
    long ContractId,
    WalletEntryType Type,
    long AmountCents,
    long BalanceCents,
    long Time
);

public sealed record ContractEscrow(long IskCents, double ItemsValue);

public sealed record ContractChange(
    long Id,
    long ContractId,
    ContractChangeKind Kind,
    IReadOnlyList<InventoryChange> Inventory,
    long? BalanceCents
);

public sealed record ContractChanges(IReadOnlyList<ContractChange> Items, long Cursor, bool More);

public sealed record ContractPageInfo(
    int Outstanding,
    IReadOnlyList<PersonalContract> Assigned,
    IReadOnlyList<long> Attention,
    IReadOnlyList<PersonalContract>? InProgress = null,
    int BiddingOn = 0
);

public sealed record ContractOwnerFilter(long OwnerId, int? Status, int Type, bool? IssuedTo, int Limit, long? BeforeId);

public interface IPilotContracts
{
    long Revision { get; }
    long Observe(long pilotId);
    ContractChanges Changes(long pilotId, long after);
    long Create(long pilotId, ContractDraft draft);
    IReadOnlyList<long> CreateBatch(long pilotId, ContractDraft draft);
    PersonalContract Accept(long pilotId, long contractId);
    void Reject(long pilotId, long contractId);
    void Delete(long pilotId, long contractId);
    void Dismiss(long pilotId, long contractId);
    void Complete(long pilotId, long contractId);
    void Fail(long pilotId, long contractId);
    CourierPackageInfo? Package(long pilotId, long itemId);
    void BreakPackage(long pilotId, long itemId);
    void Bid(long pilotId, long contractId, long amountCents);
    void FinishAuction(long pilotId, long contractId, bool issuer);
    void ForgetAuction(long pilotId, long contractId);
    IReadOnlyList<ContractDetails> Bids(long pilotId);
    ContractDetails Details(long pilotId, long contractId);
    IReadOnlyList<ContractDetails> List(long pilotId, ContractOwnerFilter filter);
    IReadOnlyList<ContractDetails> Available(long pilotId);
    ContractSearchResult Search(long pilotId, ContractSearch search);
    ContractPageInfo Page(long pilotId);
    ContractEscrow Escrow(long pilotId);
}

public static class ContractRules
{
    public const int MaximumItems = 2000;
    public const int MaximumTitle = 50;
    public const int MaximumDescription = 4000;
    public const int ChangeBatchSize = 32;
    public const long MaximumAmountCents = 1_000_000_000_000_000;
    public const long FirstJournalId = 12_000_000_000_000;
    private const long MinimumBrokerIsk = 10_000;
    private const long MaximumBrokerIsk = 100_000_000;
    private const long MinimumTaxIsk = 10_000;
    private const long MaximumTaxIsk = 1_000_000_000;
    private const long MinimumDepositIsk = 100_000;
    private const long MaximumDepositIsk = 100_000_000;
    private const int MinutesPerDay = 1440;
    private const int BaseSlots = 1;
    private const int SlotsPerSkillLevel = 4;
    private const decimal BrokerRate = 0.004m;
    private const decimal BrokerSkillReduction = 0.0002m;
    private const decimal BrokerCapReduction = 0.05m;
    private const decimal AdditionalDayMultiplier = 0.05m;
    private const decimal SalesTaxRate = 0.01m;
    private const decimal AccountingReduction = 0.001m;
    private const decimal SalesTaxCapReduction = 0.1m;
    private const decimal DepositRate = 0.01m;

    public static void Check(ContractDraft draft)
    {
        CourierRules.Check(draft);
        AuctionRules.Check(draft);
        BulkContractRules.Check(draft);
        if (
            draft.AssigneeId is <= 0
            || draft.StationId <= 0
            || draft.ExpiryMinutes
                is not (MinutesPerDay or 3 * MinutesPerDay or 7 * MinutesPerDay or 14 * MinutesPerDay or 28 * MinutesPerDay)
            || draft.PriceCents < 0
            || draft.RewardCents < 0
            || draft.PriceCents > MaximumAmountCents
            || draft.RewardCents > MaximumAmountCents
            || (draft.PriceCents > 0 && draft.RewardCents > 0)
            || draft.Title.Length > MaximumTitle
            || draft.Description.Length > MaximumDescription
            || draft.Title.Contains('\0')
            || draft.Description.Contains('\0')
            || draft.Items.Count + draft.RequestedItems.Count == 0
            || draft.Items.Count + draft.RequestedItems.Count > ContractAssetRules.MaximumSelection
            || draft.Items.Any(i => i.ItemId <= 0 || i.Quantity <= 0)
            || draft.RequestedItems.Any(i => i.TypeId <= 0 || i.Quantity <= 0)
            || draft.Items.Select(i => i.ItemId).Distinct().Count() != draft.Items.Count
            || draft.RequestedItems.Select(i => i.TypeId).Distinct().Count() != draft.RequestedItems.Count
        )
        {
            throw new InvalidDataException("Choose valid contract terms, quantities and expiry.");
        }
    }

    public static long Cents(double amount)
    {
        if (!double.IsFinite(amount) || amount < 0 || amount > (double)MaximumAmountCents / MarketDefinitions.CentsPerIsk)
        {
            throw new InvalidDataException("Choose a valid contract ISK amount.");
        }
        decimal cents = (decimal)amount * MarketDefinitions.CentsPerIsk;
        if (Math.Abs(cents - decimal.Round(cents)) > 0.0001m)
        {
            throw new InvalidDataException("Contract amounts use whole cents.");
        }
        return checked((long)decimal.Round(cents));
    }

    public static int Limit(ContractSkills skills) => BaseSlots + SlotsPerSkillLevel * (skills.Contracting + skills.AdvancedContracting);

    // Native contractscommon.CalcContractFees and CreateContractWnd's private branch.
    // Public fees truncate the base ISK calculation before the duration multiplier.
    public static ContractFees Fees(ContractDraft draft, ContractSkills skills)
    {
        if (draft.AssigneeId is not null)
        {
            return new(MinimumBrokerIsk * MarketDefinitions.CentsPerIsk, 0, 0);
        }
        decimal basis = (decimal)Math.Max(draft.PriceCents, draft.RewardCents) / MarketDefinitions.CentsPerIsk;
        decimal multiplier = draft.Type == ContractType.Courier ? CourierRules.FeeMultiplier : 1;
        int days = draft.ExpiryMinutes / MinutesPerDay - 1;
        decimal brokerRate = (BrokerRate - skills.BrokerRelations * BrokerSkillReduction) * multiplier;
        decimal broker =
            decimal.Truncate(
                Math.Clamp(basis * brokerRate, MinimumBrokerIsk, MaximumBrokerIsk * (1 - skills.BrokerRelations * BrokerCapReduction))
            ) * (1 + days * AdditionalDayMultiplier);
        decimal deposit = decimal.Truncate(Math.Clamp(basis * DepositRate * multiplier, MinimumDepositIsk * multiplier, MaximumDepositIsk));
        return new(
            checked((long)(broker * MarketDefinitions.CentsPerIsk)),
            checked((long)(deposit * MarketDefinitions.CentsPerIsk)),
            draft.Type == ContractType.Courier ? 0 : SalesTax(Math.Max(draft.PriceCents, draft.RewardCents), skills.Accounting)
        );
    }

    public static long SalesTax(long priceCents, int accounting)
    {
        decimal basis = (decimal)priceCents / MarketDefinitions.CentsPerIsk;
        decimal tax = decimal.Truncate(
            Math.Clamp(
                basis * (SalesTaxRate - accounting * AccountingReduction),
                MinimumTaxIsk,
                MaximumTaxIsk * (1 - accounting * SalesTaxCapReduction)
            )
        );
        return checked((long)(tax * MarketDefinitions.CentsPerIsk));
    }
}
