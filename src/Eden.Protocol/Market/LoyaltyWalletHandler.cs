using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

[Injectable(InjectionType.Scoped)]
internal sealed class LoyaltyWalletHandler(IPilotLoyaltyWallet wallet, TimeProvider clock) : ISelectedPilotCallHandler
{
    private long? _pilot;
    private Dictionary<int, long> _balances = [];
    private long _nextPoll;
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_pilot != context.Pilot.PilotId)
        {
            Observe(context.Pilot.PilotId, wallet.Read(context.Pilot.PilotId));
        }
    }

    public void Reset()
    {
        _pilot = null;
        _balances.Clear();
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
        if (service != "LPSvc" || method != "GetAllMyCharacterWalletLPBalances")
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Read the selected character's loyalty balances without additional arguments.");
        }
        var rows = wallet.Read(context.Pilot.PilotId);
        Observe(context.Pilot.PilotId, rows);
        return new BlueList<LoyaltyBalanceRow>(rows.Select(b => new LoyaltyBalanceRow(b)).ToArray());
    }

    private void Observe(long pilot, IReadOnlyList<LoyaltyBalance> rows)
    {
        _pilot = pilot;
        _balances = rows.ToDictionary(b => b.IssuerCorporationId, b => b.Points);
        _nextPoll = checked(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() + TimeSpan.TicksPerSecond);
    }
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
