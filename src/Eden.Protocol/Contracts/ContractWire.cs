using Eden.Gameplay.Contracts;
using Eden.Gameplay.Market;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Contracts;

internal static class ContractWire
{
    internal static BlueInstance Fields(Dictionary<string, object?> fields) => new("utillib.KeyVal", fields);

    internal static double Isk(long cents) => (double)cents / MarketDefinitions.CentsPerIsk;
}

internal sealed record ContractRow(PersonalContract Contract) : BlueModel
{
    public override object ToBlue() =>
        ContractWire.Fields(
            new()
            {
                ["contractID"] = Contract.Id,
                ["type"] = (int)ContractType.ItemExchange,
                ["issuerID"] = Contract.IssuerId,
                ["issuerCorpID"] = Contract.IssuerCorporationId,
                ["forCorp"] = false,
                ["availability"] = Contract.AssigneeId is null ? 0 : 1,
                ["assigneeID"] = Contract.AssigneeId ?? 0,
                ["acceptorID"] = Contract.AcceptorId ?? 0,
                ["status"] = (int)Contract.Status,
                ["startStationID"] = Contract.StationId,
                ["endStationID"] = 0,
                ["startSolarSystemID"] = Contract.SystemId,
                ["endSolarSystemID"] = 0,
                ["startRegionID"] = Contract.RegionId,
                ["endRegionID"] = 0,
                ["startConstellationID"] = Contract.ConstellationId,
                ["endConstellationID"] = 0,
                ["dateIssued"] = Contract.Issued,
                ["dateExpired"] = Contract.Expires,
                ["dateAccepted"] = Contract.Accepted ?? 0,
                ["dateCompleted"] = Contract.Completed ?? 0,
                ["price"] = ContractWire.Isk(Contract.PriceCents),
                ["reward"] = ContractWire.Isk(Contract.RewardCents),
                ["collateral"] = 0d,
                ["volume"] = Contract.Volume,
                ["numDays"] = 0,
                ["title"] = new BlueUnicode(Contract.Title),
                ["description"] = new BlueUnicode(Contract.Description),
                ["issuerWalletKey"] = (int)WalletAccountKey.Cash,
                ["acceptorWalletKey"] = (int)WalletAccountKey.Cash,
            }
        );
}

internal sealed record ContractItemRow(ContractItem Item) : BlueModel
{
    public override object ToBlue() =>
        ContractWire.Fields(
            new()
            {
                ["itemID"] = Item.ItemId,
                ["itemTypeID"] = Item.TypeId,
                ["quantity"] = Item.Quantity,
                ["inCrate"] = Item.Offered,
                ["parentID"] = Item.ParentId,
                ["flagID"] = Item.FlagId,
                ["singleton"] = Item.Singleton,
                ["copy"] = false,
                ["licensedProductionRunsRemaining"] = -1,
                ["materialLevel"] = 0,
                ["productivityLevel"] = 0,
                ["damage"] = Item.Damage,
            }
        );
}

internal sealed record ContractDetailsReply(ContractDetails Details) : BlueModel
{
    public override object ToBlue() =>
        ContractWire.Fields(
            new()
            {
                ["contract"] = new ContractRow(Details.Contract),
                ["items"] = new BlueList<ContractItemRow>(Details.Items.Select(i => new ContractItemRow(i)).ToArray()),
                ["bids"] = new List<object?>(),
            }
        );
}

internal sealed record ContractListReply(IReadOnlyList<ContractDetails> Contracts) : BlueModel
{
    public override object ToBlue() =>
        ContractWire.Fields(
            new()
            {
                ["contracts"] = new BlueList<ContractRow>(Contracts.Select(c => new ContractRow(c.Contract)).ToArray()),
                ["items"] = Contracts.ToDictionary(
                    c => c.Contract.Id,
                    c => (object?)new BlueList<ContractItemRow>(c.Items.Select(i => new ContractItemRow(i)).ToArray())
                ),
            }
        );
}

internal sealed record ContractAttentionRow(long ContractId) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                BlueRows.Descriptor([("contractID", BlueColumnType.Int64), ("forCorp", BlueColumnType.Boolean)]),
                new object?[] { ContractId, false },
            ]
        );
}

internal sealed record ContractLoginReply(ContractPageInfo Page) : BlueModel
{
    public override object ToBlue() =>
        ContractWire.Fields(
            new()
            {
                ["assignedToMe"] = new BlueList<ContractRow>(Page.Assigned.Select(c => new ContractRow(c)).ToArray()),
                ["needsAttention"] = new BlueList<ContractAttentionRow>(
                    Page.Attention.Select(id => new ContractAttentionRow(id)).ToArray()
                ),
                ["inProgress"] = new List<object?>(),
            }
        );
}

internal sealed record ContractPageReply(ContractPageInfo Page) : BlueModel
{
    public override object ToBlue() =>
        ContractWire.Fields(
            new()
            {
                ["numOutstandingContractsNonCorp"] = Page.Outstanding,
                ["numOutstandingContractsForCorp"] = 0,
                ["numOutstandingContracts"] = Page.Outstanding,
                ["numRequiresAttention"] = Page.Attention.Count,
                ["numRequiresAttentionCorp"] = 0,
                ["numBiddingOn"] = 0,
                ["numBiddingOnCorp"] = 0,
                ["numInProgress"] = 0,
                ["numInProgressCorp"] = 0,
                ["outstandingContracts"] = Page
                    .Assigned.Select(c =>
                        (object?)new object?[] { c.IssuerId, c.IssuerCorporationId, c.AssigneeId, (int)ContractType.ItemExchange }
                    )
                    .ToList(),
            }
        );
}

internal sealed record ContractLimitsReply(int Outstanding) : BlueModel
{
    public override object ToBlue() =>
        ContractWire.Fields(
            new()
            {
                ["myCharTotal"] = Outstanding,
                ["nonCorpForMyChar"] = Outstanding,
                ["myCorpTotal"] = 0,
                ["nonCorpForMyCorp"] = 0,
            }
        );
}
