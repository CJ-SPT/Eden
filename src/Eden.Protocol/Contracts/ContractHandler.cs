using Eden.Gameplay.Contracts;
using Eden.Gameplay.Market;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Contracts;

[Injectable(InjectionType.Scoped)]
internal sealed class ContractHandler(IPilotContracts contracts, IPilotWalletReader wallet) : ISelectedPilotCallHandler
{
    private long? _pilot;
    private long _cursor;
    private long _revision;
    private bool _more;
    private readonly HashSet<long> _assignedFetched = [];
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_pilot == context.Pilot.PilotId)
        {
            return;
        }
        _revision = contracts.Revision;
        _cursor = contracts.Observe(context.Pilot.PilotId);
        _pilot = context.Pilot.PilotId;
        _more = false;
        _assignedFetched.Clear();
    }

    public void Reset()
    {
        _pilot = null;
        _more = false;
        _assignedFetched.Clear();
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        long revision = contracts.Revision;
        if (!_more && _revision == revision)
        {
            return;
        }
        var changes = contracts.Changes(context.Pilot.PilotId, _cursor);
        _cursor = changes.Cursor;
        _more = changes.More;
        _revision = revision;
        if (changes.Items.Any(c => c.BalanceCents is not null))
        {
            // Another transaction may have updated this wallet after the queued
            // contract event. Publish its current balance once, never an old snapshot.
            context.Notifications.Add(
                new AccountBalanceChanged(context.Pilot.PilotId, ContractWire.Isk(wallet.Balance(context.Pilot.PilotId)))
            );
        }
        foreach (var change in changes.Items)
        {
            if (change.Kind == ContractChangeKind.Assigned && _assignedFetched.Remove(change.ContractId))
            {
                continue;
            }
            foreach (var item in change.Inventory)
            {
                InventoryChangesWire.NotifyItemChange(context, item);
            }
            context.Notifications.Add(new ContractEvent(change.ContractId, change.Kind));
            if (change.Kind == ContractChangeKind.Accepted)
            {
                context.Notifications.Add(new ContractEvent(change.ContractId, ContractChangeKind.Completed));
            }
        }
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "contractProxy"
            || method
                is not (
                    "CreateContract"
                    or "GetContract"
                    or "AcceptContract"
                    or "CompleteContract"
                    or "DeleteContract"
                    or "DeleteNotification"
                    or "GetLoginInfo"
                    or "GetMyContractEscrow"
                    or "CollectMyPageInfo"
                    or "NumOutstandingContracts"
                    or "GetContractListForOwner"
                    or "GetMyExpiredContractList"
                    or "GetMyCurrentContractList"
                    or "GetMyBids"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            Enter(context);
            if (
                keywords is { Count: > 0 }
                && keywords.Keys.Any(k =>
                    method switch
                    {
                        "CreateContract" => k != "confirm",
                        "GetContractListForOwner" => k is not ("num" or "startContractID"),
                        _ => true,
                    }
                )
            )
            {
                throw new InvalidDataException("Unexpected contract arguments.");
            }
            long pilot = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("CreateContract", [var draft]):
                    if (keywords?.TryGetValue("confirm", out var confirm) == true)
                    {
                        ContractRequestWire.Flag(confirm);
                    }
                    // ShowCreatedContracts calls len() and indexes [0] even for a
                    // single item exchange. Preserve its list-of-IDs return shape.
                    return new BlueList<long>([contracts.Create(pilot, ContractRequestWire.Draft(draft))]);
                case ("GetContract", [long id]):
                    return new ContractDetailsReply(contracts.Details(pilot, id));
                case ("AcceptContract", [long id, var corporation]):
                    Personal(corporation);
                    return new ContractRow(contracts.Accept(pilot, id));
                case ("CompleteContract", [long id, (long)ContractStatus.Rejected]):
                    contracts.Reject(pilot, id);
                    return true;
                case ("DeleteContract", [long id]):
                    contracts.Delete(pilot, id);
                    return true;
                case ("DeleteNotification", [long id, var corporation]):
                    Personal(corporation);
                    contracts.Dismiss(pilot, id);
                    return PilotCallResult.None;
                case ("GetLoginInfo", []):
                    var page = contracts.Page(pilot);
                    _assignedFetched.UnionWith(page.Assigned.Select(c => c.Id));
                    return new ContractLoginReply(page);
                case ("GetMyContractEscrow", []):
                    var escrow = contracts.Escrow(pilot);
                    return new EscrowInfo(ContractWire.Isk(escrow.IskCents), escrow.ItemsValue);
                case ("CollectMyPageInfo", []):
                    return new ContractPageReply(contracts.Page(pilot));
                case ("NumOutstandingContracts", []):
                    return new ContractLimitsReply(contracts.Page(pilot).Outstanding);
                case ("GetContractListForOwner", [long owner, var status, var type, var issuedTo]):
                    return new ContractListReply(
                        contracts.List(
                            pilot,
                            new(
                                owner,
                                status is null ? null : ContractRequestWire.Number(status),
                                type is null ? 0 : ContractRequestWire.Number(type),
                                issuedTo is null ? null : ContractRequestWire.Flag(issuedTo),
                                keywords?.GetValueOrDefault("num") is { } num
                                    ? ContractRequestWire.Number(num)
                                    : ContractSearchRules.PageSize,
                                keywords?.GetValueOrDefault("startContractID") is { } before ? ContractRequestWire.Id(before) : null
                            )
                        )
                    );
                case ("GetMyExpiredContractList", [var corporation]):
                    if (ContractRequestWire.Flag(corporation))
                    {
                        return new ContractListReply([]);
                    }
                    return new ContractListReply(
                        contracts.List(pilot, new(pilot, -1, 0, false, ContractAssetRules.MaximumSelection, null))
                    );
                case ("GetMyCurrentContractList", [var accepted, var corporation]):
                    bool forCorp = ContractRequestWire.Flag(corporation);
                    bool isAccepted = ContractRequestWire.Flag(accepted);
                    return new ContractListReply(
                        forCorp || isAccepted
                            ? []
                            : contracts.List(
                                pilot,
                                new(pilot, (int)ContractStatus.Outstanding, 0, false, ContractAssetRules.MaximumSelection, null)
                            )
                    );
                case ("GetMyBids", [var corporation]):
                    ContractRequestWire.Flag(corporation);
                    return new ContractListReply([]);
                default:
                    return new PilotCallResult.Failure("Invalid item-exchange contract request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("This contract amount or identifier is too large.");
        }
    }

    private static void Personal(object? corporation)
    {
        if (ContractRequestWire.Flag(corporation))
        {
            throw new InvalidDataException("Corporation contracts are not available yet.");
        }
    }
}

internal sealed record ContractEvent(long ContractId, ContractChangeKind Kind)
    : PilotNotification(
        Kind switch
        {
            ContractChangeKind.Created => "OnContractCreated",
            ContractChangeKind.Assigned => "OnContractAssigned",
            ContractChangeKind.Accepted => "OnContractAccepted",
            ContractChangeKind.Rejected => "OnContractRejected",
            ContractChangeKind.Reclaimed => "OnContractEscrowChanged",
            ContractChangeKind.Completed => "OnContractCompleted",
            _ => throw new InvalidDataException("Unknown contract change."),
        }
    )
{
    public override object?[] Arguments => Kind is ContractChangeKind.Created or ContractChangeKind.Reclaimed ? [] : [ContractId];
}
