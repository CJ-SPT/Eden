using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Redemption;

[Injectable(InjectionType.Scoped)]
internal sealed class RedemptionHandler(
    IAccountRedemption redemption,
    TimeProvider clock,
    FreeSkillPointHandler freePoints,
    LoyaltyWalletHandler loyalty
) : IPilotCallHandler
{
    private long[]? _tokens;
    private long _nextPoll;
    public bool IsMonitoring => _tokens is not null;

    public void Advance(PilotCallContext context)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (_tokens is null || now < _nextPoll)
        {
            return;
        }
        var current = redemption.Read(context.AccountId).Select(t => t.Id).ToArray();
        if (!_tokens.SequenceEqual(current))
        {
            context.Notifications.Add(new RedeemingQueueUpdated());
        }
        _tokens = current;
        _nextPoll = checked(now + TimeSpan.TicksPerSecond);
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        if (service != "userSvc" || method is not ("GetRedeemTokens" or "ClaimRedeemTokens" or "TrashRedeemTokens" or "ReverseRedeem"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (arguments is not object?[] args)
            {
                throw new InvalidDataException("Invalid redemption arguments.");
            }
            bool home = false;
            if (keywords is { Count: > 0 })
            {
                if (
                    method != "ClaimRedeemTokens"
                    || keywords.Count != 1
                    || !keywords.TryGetValue("useHomeStation", out var value)
                    || value is not bool chosen
                )
                {
                    throw new InvalidDataException("Invalid redemption options.");
                }
                home = chosen;
            }
            switch (method, args)
            {
                case ("GetRedeemTokens", []):
                    var tokens = redemption.Read(context.AccountId);
                    Observe(tokens);
                    return new BlueList<RedeemTokenRow>(tokens.Select(t => new RedeemTokenRow(t)).ToArray());
                case ("ClaimRedeemTokens", [List<object?> requested, long pilot]):
                    var pending = redemption.Read(context.AccountId).ToDictionary(t => t.Id, t => t.TypeId);
                    var selected = requested.Select(value => Selection(value, pending)).ToArray();
                    var result = redemption.Claim(context.AccountId, pilot, selected, home);
                    if (context.Selected && context.Pilot.PilotId == pilot)
                    {
                        var cash = result.Currencies.LastOrDefault(r => r.Kind == RedeemCurrencyKind.Isk);
                        if (cash is not null)
                        {
                            context.Notifications.Add(new AccountBalanceChanged(pilot, (double)cash.After / MarketDefinitions.CentsPerIsk));
                        }
                        loyalty.Notify(context, result.Currencies);
                        if (
                            result.FreeSkillPoints is long balance
                            && result.Training is { } state
                            && result.FreePointRevision is long revision
                        )
                        {
                            freePoints.Notify(context, balance, revision, state);
                        }
                        foreach (
                            var item in result.Items.Where(i =>
                                i.Item.LocationId == context.Pilot.StationId && context.Pilot.Position is null
                            )
                        )
                        {
                            InventoryChangesWire.NotifyItemChange(context, new(item.Item, null));
                        }
                    }
                    if (result.Items.Count > 0 || result.SkillPoints > 0 || result.Currencies.Count > 0)
                    {
                        Changed(context);
                    }
                    return new RedeemClaimReply(result);
                case ("TrashRedeemTokens", [List<object?> requested]):
                    redemption.Trash(context.AccountId, requested.Select(Key).ToArray());
                    Changed(context);
                    return PilotCallResult.None;
                case ("ReverseRedeem", [long item]) when context.Selected:
                    var change = redemption.Reverse(context.AccountId, context.Pilot.PilotId, item);
                    InventoryChangesWire.NotifyItemChange(context, change);
                    Changed(context);
                    return PilotCallResult.None;
                default:
                    return new PilotCallResult.Failure("Invalid redemption request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("This redemption quantity or timestamp is too large.");
        }
    }

    private void Observe(IReadOnlyList<RedeemToken> tokens)
    {
        _tokens = tokens.Select(t => t.Id).ToArray();
        _nextPoll = checked(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() + TimeSpan.TicksPerSecond);
    }

    private void Changed(PilotCallContext context)
    {
        Observe(redemption.Read(context.AccountId));
        context.Notifications.Add(new RedeemingQueueUpdated());
    }

    private static RedeemSelection Selection(object? value, IReadOnlyDictionary<long, int> pending)
    {
        // RedeemService._HandleMctTokens converts UI dictionaries into
        // (tokenID, massTokenID) tuples before the build-3662168 remote call.
        // Resolve the type from this account's issued tokens, never from a guess.
        if (value is object?[] pair && pair is [long tokenId, null] && tokenId > 0)
        {
            return pending.TryGetValue(tokenId, out int issuedType)
                ? new(tokenId, issuedType)
                : throw new InvalidDataException("This redeem token is unavailable or expired.");
        }
        if (
            value is not Dictionary<string, object?> row
            || row.Count != 3
            || !row.TryGetValue("tokenID", out var token)
            || token is not long id
            || id <= 0
            || !row.TryGetValue("massTokenID", out var mass)
            || mass is not null
            || !row.TryGetValue("typeID", out var type)
            || type is not long typeId
            || typeId is <= 0 or > int.MaxValue
        )
        {
            throw new InvalidDataException("Choose an issued personal redeem token.");
        }
        return new(id, (int)typeId);
    }

    private static long Key(object? value) =>
        value is object?[] pair && pair is [long id, null] && id > 0
            ? id
            : throw new InvalidDataException("Choose an issued personal redeem token.");
}

internal sealed record RedeemingQueueUpdated() : PilotNotification("OnRedeemingQueueUpdated")
{
    public override object?[] Arguments => [];
}

internal sealed record RedeemTokenRow(RedeemToken Token) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["tokenID"] = Token.Id,
                ["massTokenID"] = null,
                ["typeID"] = Token.TypeId,
                ["quantity"] = Token.Quantity,
                ["label"] = Token.Label,
                ["description"] = Token.Description,
                ["stationID"] = Token.StationId,
                ["dateTime"] = Token.CreatedAt,
                ["expireDateTime"] = Token.ExpiresAt,
                ["soulbound"] = false,
                ["isAutoInject"] = SkillPointRewards.Amount(Token.TypeId) > 0 || CurrencyRewardRules.Kind(Token.TypeId).HasValue,
                ["blueprintInfo"] = null,
            }
        );
}

internal sealed record RedeemClaimReply(RedeemResult Result) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["charID"] = Result.PilotId,
            ["stationID"] = Result.Items.Select(i => i.Item.LocationId).Distinct().ToArray() is [long station] ? station : null,
            ["skillpoints"] = Result.SkillPoints,
            ["itemsCreated"] = Result.Items.Select(i => (object?)new RedeemedItemRow(i)).ToList(),
            ["errors"] = Result
                .Errors.Select(e =>
                    (object?)
                        new object?[]
                        {
                            e.TokenId,
                            new BlueConstructor("exceptions.UserError", ["CustomInfo", new RedeemErrorValues(e.Information, e.TypeId)]),
                        }
                )
                .ToList(),
        };
}

internal sealed record RedeemedItemRow(RedeemedItem Redeemed) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["itemID"] = Redeemed.Item.Id,
            ["typeID"] = Redeemed.Item.TypeId,
            ["quantity"] = Redeemed.Item.Quantity,
            ["stationID"] = Redeemed.Item.LocationId,
        };
}

internal sealed record RedeemErrorValues(string Information, int TypeId) : BlueModel
{
    public override object ToBlue() => new Dictionary<string, object?> { ["info"] = Information, ["itemTypeID"] = TypeId };
}
