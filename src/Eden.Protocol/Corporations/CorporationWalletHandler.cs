using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable(InjectionType.Scoped)]
internal sealed class CorporationWalletHandler(
    ICorporationWallet wallet,
    IPlayerCorporations corporations,
    WalletBalanceMonitor personal,
    TimeProvider clock
) : ISelectedPilotCallHandler
{
    private int? _corporation;
    private Dictionary<int, long> _balances = [];

    public void Reset()
    {
        _corporation = null;
        _balances.Clear();
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

    // WalletHandler owns account.GetCashBalance/GetTransactions in both modes.
    internal PilotCallResult Read(PilotCallContext context, string method, object?[] args, Dictionary<string, object?>? keywords) =>
        Dispatch(context, method, args, keywords);

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
                case ("GetCashBalance", [_]):
                    CheckKeywords(keywords, "accountKey");
                    int accountKey = Key(keywords?.GetValueOrDefault("accountKey"), context.Pilot.Identity.CorporationAccountKey);
                    return Isk(wallet.Balance(actor, accountKey));
                case ("SetAccountKey", [long key])
                    when key is >= CorporationWalletRules.MasterKey and <= CorporationWalletRules.LastKey
                        && keywords is null or { Count: 0 }:
                    wallet.SelectDivision(actor, (int)key);
                    context.Flight.UpdateCorporation(corporations.Identity(actor));
                    return PilotCallResult.None;
                case ("GetTransactions", [long key, var year, var month, true])
                    when key is >= CorporationWalletRules.MasterKey and <= CorporationWalletRules.LastKey
                        && keywords is null or { Count: 0 }:
                    if (
                        !(year is null && month is null)
                        && !(year is long y && y is >= 1601 and <= 9999 && month is long m && m is >= 1 and <= 12)
                    )
                    {
                        throw new InvalidDataException("Choose a valid wallet journal date.");
                    }
                    WalletJournalFilter filter = new(year is long yr ? (int)yr : null, month is long mo ? (int)mo : null);
                    return new BlueList<WalletJournalEntry>(
                        wallet
                            .Journal(actor, true, (int)key)
                            .Where(e => filter.Includes(e.Time, clock.GetUtcNow().UtcDateTime))
                            .Select(Journal)
                            .ToArray()
                    );
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

    private static WalletJournalEntry Journal(CorporationWalletEntry entry) =>
        new(
            CorporationWalletRules.FirstJournalId + entry.Id,
            entry.Time,
            (WalletEntryType)entry.EntryType,
            entry.AmountCents < 0 ? entry.OwnerId : entry.CounterpartyId,
            entry.AmountCents < 0 ? entry.CounterpartyId : entry.OwnerId,
            entry.ReferenceId,
            entry.AmountCents,
            entry.BalanceCents,
            entry.Reason
        );

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

    private static double Isk(long cents) => (double)cents / MarketDefinitions.CentsPerIsk;

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
