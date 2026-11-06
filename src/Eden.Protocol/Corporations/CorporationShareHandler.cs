using Eden.Gameplay.Corporations;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

internal sealed partial class CorporationHandler
{
    private PilotCallResult CallShares(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords
    )
    {
        if (
            service != "corpRegistry"
            || method is not ("GetSharesByShareholder" or "GetShareholders" or "MoveCompanyShares" or "MovePrivateShares")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected share arguments.");
            }
            switch (method, args)
            {
                case ("GetSharesByShareholder", [bool company]):
                    return ShareRows(shares.Holdings(context.Pilot.PilotId, company));
                case ("GetSharesByShareholder", [0L or 1L]):
                    return ShareRows(shares.Holdings(context.Pilot.PilotId, Equals(args[0], 1L)));
                case ("GetShareholders", [long corporation]) when corporation is > 0 and <= int.MaxValue:
                    return new CorporationShareholdersReply(shares.Shareholders(context.Pilot.PilotId, (int)corporation));
                case ("MoveCompanyShares" or "MovePrivateShares", [long issuer, long recipient, long quantity])
                    when issuer is > 0 and <= int.MaxValue:
                    shares.Transfer(context.Pilot.PilotId, new((int)issuer, method == "MoveCompanyShares", recipient, quantity));
                    Advance(context);
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Choose a share issuer, recipient and a positive whole number of shares.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static CompatibilityRowset ShareRows(IReadOnlyList<CorporationShareHolding> holdings) =>
        new(CorporationShareLine.Header, holdings.Select(h => (IBlueWritable)new CorporationShareLine(h)).ToArray());
}

internal sealed record CorporationShareLine(CorporationShareHolding Holding) : BlueModel
{
    internal static readonly string[] Header = ["corporationID", "shareholderID", "shares", "shareholderCorporationID"];

    public override object ToBlue() =>
        new List<object?> { Holding.CorporationId, Holding.ShareholderId, Holding.Shares, Holding.ShareholderCorporationId };
}

internal sealed record CorporationShareholdersReply(IReadOnlyList<CorporationShareHolding> Holdings) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.IndexRowset",
            new Dictionary<string, object?>
            {
                ["header"] = new BlueList<string>(CorporationShareLine.Header),
                ["idName"] = "shareholderID",
                ["items"] = Holdings.ToDictionary(h => h.ShareholderId, h => (object?)new CorporationShareLine(h)),
            }
        );
}

internal sealed record CorporationShareCacheInvalidated(CorporationShareChange Change) : PilotNotification("OnShareChange")
{
    // The pinned bco_shares invalidation path accepts a deletion dictionary;
    // its update path passes a dictionary to IndexRowset.__setitem__, which
    // requires a Row/list. Evict this certificate, then the visible wallet
    // panel's OnShareChange re-reads the authoritative holdings via RPC.
    public override object?[] Arguments =>
        [
            Change.ShareholderId,
            Change.CorporationId,
            new Dictionary<string, object?>
            {
                ["corporationID"] = new object?[] { Change.CorporationId, null },
                ["shareholderID"] = new object?[] { Change.ShareholderId, null },
                ["shares"] = new object?[] { Change.Before, null },
            },
        ];
}
