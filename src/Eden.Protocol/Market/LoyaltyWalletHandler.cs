using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

[Injectable(InjectionType.Scoped)]
internal sealed class LoyaltyWalletHandler(
    IPilotLoyaltyWallet wallet,
    TimeProvider clock,
    ICorporationLoyaltyWallet corporate,
    IPlayerCorporations corporations
) : ISelectedPilotCallHandler
{
    private long? _pilot;
    private Dictionary<int, long> _balances = [];
    private long _nextPoll;
    private int? _corporation;
    private Dictionary<int, long> _corporateBalances = [];
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_pilot != context.Pilot.PilotId)
        {
            Observe(context.Pilot.PilotId, wallet.Read(context.Pilot.PilotId));
        }
        if (_corporation != context.Pilot.CorpId)
        {
            _corporation = context.Pilot.CorpId;
            _corporateBalances = corporate.Read(context.Pilot.PilotId).ToDictionary(b => b.IssuerCorporationId, b => b.Points);
        }
    }

    public void Reset()
    {
        _pilot = null;
        _balances.Clear();
        _corporation = null;
        _corporateBalances.Clear();
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (now < _nextPoll)
        {
            return;
        }
        var rows = wallet.Read(context.Pilot.PilotId);
        var current = rows.ToDictionary(b => b.IssuerCorporationId, b => b.Points);
        foreach (int issuer in _balances.Keys.Union(current.Keys).Order())
        {
            long before = _balances.GetValueOrDefault(issuer);
            long after = current.GetValueOrDefault(issuer);
            if (before != after)
            {
                context.Notifications.Add(new LoyaltyBalanceChanged(issuer, before, after));
            }
        }
        Observe(context.Pilot.PilotId, rows);
        var corporationRows = corporate.Read(context.Pilot.PilotId).ToDictionary(b => b.IssuerCorporationId, b => b.Points);
        foreach (int issuer in _corporateBalances.Keys.Union(corporationRows.Keys).Order())
        {
            if (_corporateBalances.GetValueOrDefault(issuer) != corporationRows.GetValueOrDefault(issuer))
            {
                context.Notifications.Add(new CorporationLoyaltyBalanceChanged(issuer));
            }
        }
        _corporateBalances = corporationRows;
    }

    public void Notify(PilotCallContext context, IReadOnlyList<RedeemedCurrency> rewards)
    {
        var marks = rewards.Where(r => r.Kind == RedeemCurrencyKind.EverMarks).ToArray();
        if (marks.Length == 0)
        {
            return;
        }
        Enter(context);
        context.Notifications.Add(new LoyaltyBalanceChanged(CurrencyRewardRules.ParagonCorporation, marks[0].Before, marks[^1].After));
        _balances[CurrencyRewardRules.ParagonCorporation] = marks[^1].After;
        _nextPoll = checked(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() + TimeSpan.TicksPerSecond);
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "charMgr" && method == "GetPrivateInfoOnCorpChange")
        {
            if (args is not [long id] || id != context.Pilot.PilotId || keywords is { Count: > 0 })
            {
                return new PilotCallResult.Failure("Read only your own private corporation membership.");
            }
            var identity = corporations.Identity(id);
            return corporations.Read(identity.Id) is null ? PilotCallResult.None : new PrivateCorporationMembership(identity.JoinedAt);
        }
        if (
            service != "LPSvc"
            || method
                is not (
                    "GetAllMyCharacterWalletLPBalances"
                    or "GetAllMyCorporationWalletLPBalances"
                    or "TransferLPFromMyWalletToOtherCorp"
                    or "TransferLPFromMyCorpWalletToOtherCorp"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (method is "TransferLPFromMyWalletToOtherCorp" or "TransferLPFromMyCorpWalletToOtherCorp")
        {
            try
            {
                if (
                    keywords is { Count: > 0 }
                    || args is not [long recipient, long issuer, long points]
                    || recipient is <= 0 or > int.MaxValue
                    || issuer is <= 0 or > int.MaxValue
                )
                {
                    throw new InvalidDataException("Choose an LP recipient, issuer and positive whole-point amount.");
                }
                Enter(context);
                bool fromCorporation = method == "TransferLPFromMyCorpWalletToOtherCorp";
                var transfer = corporate.Transfer(context.Pilot.PilotId, fromCorporation, (int)recipient, (int)issuer, points);
                if (!fromCorporation)
                {
                    context.Notifications.Add(new LoyaltyBalanceChanged((int)issuer, transfer.SourceBefore, transfer.SourceAfter));
                    _balances[(int)issuer] = transfer.SourceAfter;
                }
                if (fromCorporation || recipient == context.Pilot.CorpId)
                {
                    context.Notifications.Add(new CorporationLoyaltyBalanceChanged((int)issuer));
                }
                _corporateBalances = corporate.Read(context.Pilot.PilotId).ToDictionary(b => b.IssuerCorporationId, b => b.Points);
                return PilotCallResult.None;
            }
            catch (InvalidDataException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Read the selected character's loyalty balances without additional arguments.");
        }
        var rows =
            method == "GetAllMyCharacterWalletLPBalances" ? wallet.Read(context.Pilot.PilotId) : corporate.Read(context.Pilot.PilotId);
        if (method == "GetAllMyCharacterWalletLPBalances")
        {
            Observe(context.Pilot.PilotId, rows);
        }
        return new BlueList<LoyaltyBalanceRow>(rows.Select(b => new LoyaltyBalanceRow(b)).ToArray());
    }

    private void Observe(long pilot, IReadOnlyList<LoyaltyBalance> rows)
    {
        _pilot = pilot;
        _balances = rows.ToDictionary(b => b.IssuerCorporationId, b => b.Points);
        _nextPoll = checked(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() + TimeSpan.TicksPerSecond);
    }
}

internal sealed record CorporationLoyaltyBalanceChanged(int IssuerCorporationId) : PilotNotification("OnCorpLPChange")
{
    public override object?[] Arguments => [null, IssuerCorporationId];
}

internal sealed record PrivateCorporationMembership(long JoinedAt) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["corporationDateTime"] = JoinedAt });
}

internal sealed record LoyaltyBalanceRow(LoyaltyBalance Balance) : BlueModel
{
    public override object ToBlue() => new object?[] { Balance.IssuerCorporationId, Balance.Points };
}

internal sealed record LoyaltyBalanceChanged(int IssuerCorporationId, long Before, long After) : PilotNotification("OnLPChange")
{
    // Build 3662168's only OnLPChange consumer ignores its first change-type parameter.
    public override object?[] Arguments => [null, IssuerCorporationId, Before, After];
}
