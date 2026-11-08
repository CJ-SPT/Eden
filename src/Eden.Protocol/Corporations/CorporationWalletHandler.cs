using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable(InjectionType.Scoped)]
internal sealed class CorporationWalletHandler(ICorporationWallet wallet, IPlayerCorporations corporations, WalletBalanceMonitor personal)
    : ISelectedPilotCallHandler
{
    private int? _corporation;
    private Dictionary<int, long> _balances = [];

    public void Reset()
    {
        _corporation = null;
        _balances.Clear();
    }

    public void Notify(PilotCallContext context, int corporationId, int key, long cents)
    {
        context.Notifications.Add(new CorporationBalanceChanged(corporationId, key, cents));
        _balances[key] = cents;
    }

    public void Enter(PilotCallContext context)
    {
        if (_corporation != context.Pilot.CorpId)
        {
            _corporation = context.Pilot.CorpId;
            _balances = corporations.Read(_corporation.Value) is null
                ? []
                : wallet.Divisions(context.Pilot.PilotId).ToDictionary(d => d.Key, d => d.BalanceCents);
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        if (corporations.Read(context.Pilot.CorpId) is null)
        {
            return;
        }
        var current = wallet.Divisions(context.Pilot.PilotId).ToDictionary(d => d.Key, d => d.BalanceCents);
        foreach (var division in current)
        {
            if (_balances.TryGetValue(division.Key, out long before) && before != division.Value)
            {
                context.Notifications.Add(new CorporationBalanceChanged(context.Pilot.CorpId, division.Key, division.Value));
            }
        }
        _balances = current;
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool selectedKey = service == "corpRegistry" && method == "SetAccountKey";
        if (!selectedKey && (service != "account" || method is not ("GiveCash" or "GiveCashFromCorpAccount" or "GetWalletDivisionsInfo")))
        {
            return PilotCallResult.Unsupported;
        }
        return Dispatch(context, method, args, keywords);
    }

    private PilotCallResult Dispatch(PilotCallContext context, string method, object?[] args, Dictionary<string, object?>? keywords)
    {
        try
        {
            long actor = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetWalletDivisionsInfo", []) when keywords is null or { Count: 0 }:
                    return new BlueList<CorporationDivisionBalanceReply>(
                        wallet.Divisions(actor).Select(d => new CorporationDivisionBalanceReply(d)).ToArray()
                    );
                case ("SetAccountKey", [long key])
                    when key is >= CorporationWalletRules.MasterKey and <= CorporationWalletRules.LastKey
                        && keywords is null or { Count: 0 }:
                    wallet.SelectDivision(actor, (int)key);
                    context.Flight.UpdateCorporation(corporations.Identity(actor));
                    return PilotCallResult.None;
                case ("GiveCash", [long recipient, var amount, var reason]):
                    CheckKeywords(keywords, "toAccountKey");
                    wallet.Transfer(
                        actor,
                        new(
                            false,
                            CorporationWalletRules.MasterKey,
                            recipient,
                            Key(keywords?.GetValueOrDefault("toAccountKey"), CorporationWalletRules.MasterKey),
                            Cents(amount),
                            Text(reason)
                        )
                    );
                    personal.Notify(context);
                    Advance(context);
                    return PilotCallResult.None;
                case ("GiveCashFromCorpAccount", [long recipient, var amount, long fromKey])
                    when fromKey is >= CorporationWalletRules.MasterKey and <= CorporationWalletRules.LastKey:
                    CheckKeywords(keywords, "toAccountKey", "reason");
                    wallet.Transfer(
                        actor,
                        new(
                            true,
                            (int)fromKey,
                            recipient,
                            Key(keywords?.GetValueOrDefault("toAccountKey"), CorporationWalletRules.MasterKey),
                            Cents(amount),
                            keywords?.GetValueOrDefault("reason") is { } corporationReason ? Text(corporationReason) : ""
                        )
                    );
                    personal.Notify(context);
                    Advance(context);
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid corporation wallet request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static void CheckKeywords(Dictionary<string, object?>? keywords, params string[] allowed)
    {
        if (keywords is not null && keywords.Keys.Any(key => !allowed.Contains(key)))
        {
            throw new InvalidDataException("Unexpected wallet arguments.");
        }
    }

    private static int Key(object? value, int fallback) =>
        value switch
        {
            null => fallback,
            long key when key is >= CorporationWalletRules.MasterKey and <= CorporationWalletRules.LastKey => (int)key,
            _ => throw new InvalidDataException("Choose a corporation wallet division."),
        };

    private static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Choose a wallet transfer reason.");

    private static long Cents(object? value)
    {
        double isk = value switch
        {
            long number => number,
            double number => number,
            _ => throw new InvalidDataException("Choose a positive ISK amount."),
        };
        if (!double.IsFinite(isk) || isk <= 0 || isk > long.MaxValue / (double)MarketDefinitions.CentsPerIsk)
        {
            throw new InvalidDataException("Choose a positive ISK amount within the wallet limit.");
        }
        decimal cents = (decimal)isk * MarketDefinitions.CentsPerIsk;
        if (cents != decimal.Truncate(cents) || cents > long.MaxValue)
        {
            throw new InvalidDataException("Wallet transfers allow at most two decimal places.");
        }
        return (long)cents;
    }
}

internal sealed record CorporationDivisionBalanceReply(CorporationWalletDivision Division) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["key"] = Division.Key,
                ["balance"] = (double)Division.BalanceCents / MarketDefinitions.CentsPerIsk,
            }
        );
}

internal sealed record CorporationBalanceChanged(int CorporationId, int Key, long BalanceCents) : PilotNotification("OnAccountChange")
{
    public override object?[] Arguments =>
        [
            Key == CorporationWalletRules.MasterKey ? "cash" : $"cash{Key - CorporationWalletRules.MasterKey + 1}",
            CorporationId,
            (double)BalanceCents / MarketDefinitions.CentsPerIsk,
        ];
}
