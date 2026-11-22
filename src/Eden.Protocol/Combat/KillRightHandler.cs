using Eden.Gameplay.Combat;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Combat;

[Injectable]
internal sealed class KillRightHandler(IPilotKillRights rights) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service is not ("bountyProxy" or "killRightMgr"))
        {
            return PilotCallResult.Unsupported;
        }
        long pilot = context.Pilot.PilotId;
        try
        {
            return (service, method) switch
            {
                ("bountyProxy", "GetBounties") when args is [HashSet<long> owners] => ZeroBounties(owners),
                ("bountyProxy", "GetBountiesAndKillRights")
                    when args is [HashSet<long> owners, List<object?> ids] && keywords is null or { Count: 0 } =>
                    new BountiesAndKillRightsReply(ZeroBounties(owners), Replies(rights.Available(pilot, BlueRead.PositiveIds(ids)))),
                ("bountyProxy", "GetMyKillRights") when args.Length == 0 && keywords is null or { Count: 0 } => Replies(rights.Mine(pilot)),
                ("bountyProxy", "GetKillRightsOnCharacters") when args is [List<object?> ids] && keywords is null or { Count: 0 } =>
                    Replies(rights.Available(pilot, BlueRead.PositiveIds(ids))),
                ("bountyProxy", "SellKillRight")
                    when args is [long and > 0 and var id, var price, null or (long and > 0)] && keywords is null or { Count: 0 } =>
                    Replies([rights.ListForSale(pilot, id, Price(price), (long?)args[2])]),
                ("bountyProxy", "CancelSellKillRight")
                    when args is [long and > 0 and var id, long and > 0 and var target] && keywords is null or { Count: 0 } => Cancel(
                    pilot,
                    id,
                    target
                ),
                ("killRightMgr", "ActivateKillRight") when args is [long and > 0 and var id] && keywords is null or { Count: 0 } =>
                    Activate(context, id, null),
                ("killRightMgr", "BuyKillRight") when args is [long and > 0 and var id, var price] && keywords is null or { Count: 0 } =>
                    Activate(context, id, Price(price)),
                (
                    "bountyProxy",
                    "GetMyKillRights"
                        or "GetKillRightsOnCharacters"
                        or "GetBountiesAndKillRights"
                        or "SellKillRight"
                        or "CancelSellKillRight"
                )
                or ("killRightMgr", "ActivateKillRight" or "BuyKillRight") => new PilotCallResult.Failure("Invalid kill-right request."),
                _ => PilotCallResult.Unsupported,
            };
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private PilotCallResult Cancel(long pilot, long id, long target)
    {
        rights.CancelSale(pilot, id, target);
        return PilotCallResult.None;
    }

    private PilotCallResult Activate(PilotCallContext context, long id, long? quote)
    {
        var pilot = context.Pilot;
        if (pilot.Position is null || context.ControlledStructureId.HasValue || context.Flight.InWarp)
        {
            return new PilotCallResult.Failure("You must be in space with the target to activate a kill right.");
        }
        var presence = new KillRightPresence(
            pilot.SystemId,
            pilot.ShipId,
            context.Flight.VisiblePilots().Select(p => p.ShipId).ToHashSet()
        );
        switch (rights.Activate(pilot.PilotId, id, presence, quote))
        {
            case KillRightActivationResult.Activated:
                context.Notifications.Add(new KillRightActivated());
                return PilotCallResult.None;
            case KillRightActivationResult.Expired:
                return new PilotCallResult.NamedFailure("KillRightExpired", BlueEmpty.Dictionary);
            case KillRightActivationResult.NotForSale:
                return new PilotCallResult.NamedFailure("KillRightNotForSale", BlueEmpty.Dictionary);
            case KillRightActivationResult.PriceChanged:
                return new PilotCallResult.NamedFailure("NoValidKillRight", BlueEmpty.Dictionary);
            case KillRightActivationResult.TargetNotVisible:
                return new PilotCallResult.Failure("You must be in space with the target to activate a kill right.");
            default:
                return new PilotCallResult.NamedFailure("NoValidKillRight", BlueEmpty.Dictionary);
        }
    }

    private static long Price(object? value)
    {
        double isk = value switch
        {
            long whole => whole,
            double amount => amount,
            _ => double.NaN,
        };
        if (!double.IsFinite(isk) || isk < 0 || isk > KillRightRules.MaximumPriceIsk)
        {
            throw new InvalidDataException("Enter a valid kill-right price.");
        }
        return decimal.ToInt64(decimal.Round((decimal)isk * MarketDefinitions.CentsPerIsk, 0, MidpointRounding.AwayFromZero));
    }

    private static BlueList<KillRightReply> Replies(IEnumerable<KillRight> rows) =>
        new(rows.Select(row => new KillRightReply(row)).ToArray());

    private static BlueList<BountyReply> ZeroBounties(HashSet<long> owners) =>
        new(owners.Order().Select(id => new BountyReply(id)).ToArray());
}

internal sealed record KillRightReply(KillRight Right) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["killRightID"] = Right.Id,
                ["fromID"] = Right.OwnerId,
                ["toID"] = Right.TargetId,
                ["expiryTime"] = Right.ExpiresAt,
                ["price"] = Right.PriceCents is long cents ? (double)cents / MarketDefinitions.CentsPerIsk : null,
                ["restrictedTo"] = Right.RestrictedTo,
            }
        );
}

internal sealed record BountiesAndKillRightsReply(BlueList<BountyReply> Bounties, BlueList<KillRightReply> Rights) : BlueModel
{
    public override object ToBlue() => new object?[] { Bounties.ToBlue(), Rights.ToBlue() };
}

internal sealed record BountyReply(long TargetId) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            TargetId,
            new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["targetID"] = TargetId, ["bounty"] = 0d }),
        };
}

internal sealed record KillRightActivated() : PilotNotification("OnKillRightActivated")
{
    public override object?[] Arguments => [];
}
