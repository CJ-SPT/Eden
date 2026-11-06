using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Gameplay.Repair;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

[Injectable]
internal sealed class WalletHandler(IPilotWalletReader wallet, TimeProvider clock, ICorporationWallet corporationWallet)
    : ISelectedPilotCallHandler
{
    private static double Isk(long cents) => (double)cents / MarketDefinitions.CentsPerIsk;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "account")
        {
            return PilotCallResult.Unsupported;
        }
        if (method == "GetCashBalance" && args is [true] or [1L] || method == "GetTransactions" && args is [_, _, _, true])
        {
            return ReadCorporation(context, method, args, keywords);
        }
        if (method == "GetEntryTypes" && args.Length == 0)
        {
            return new BlueList<WalletEntryTypeReply>(wallet.EntryTypes().Select(type => new WalletEntryTypeReply(type)).ToArray());
        }
        if (method == "GetCashBalance" && args is [] or [0L] or [false])
        {
            return Isk(wallet.Balance(context.Pilot.PilotId));
        }
        if (method != "GetTransactions" || args is not [(long)WalletAccountKey.Cash, var year, var month, false])
        {
            return PilotCallResult.Unsupported;
        }
        // The default Last 30 Days filter passes (None, None); month filters
        // pass a real year and one-based month in build 3662168.
        bool recent = year is null && month is null;
        if (!recent && (year is not long y || y is < 1601 or > 9999 || month is not long m || m is < 1 or > 12))
        {
            return PilotCallResult.Unsupported;
        }
        WalletJournalFilter filter = new(
            year is long yearNumber ? (int)yearNumber : null,
            month is long monthNumber ? (int)monthNumber : null
        );
        var journal = wallet.Journal(context.Pilot.PilotId, filter.Period(clock.GetUtcNow().UtcDateTime));
        List<WalletJournalEntry> entries = [];
        foreach (var trade in journal.Trades)
        {
            if (!filter.Includes(trade.Time, clock.GetUtcNow().UtcDateTime))
            {
                continue;
            }
            long gross = checked(trade.PriceCents * trade.Quantity);
            if (trade.TaxCents > 0)
            {
                entries.Add(Journal(trade.Id * 2 + 1, trade, WalletEntryType.TransactionTax, -trade.TaxCents, trade.BalanceCents));
            }
            entries.Add(
                Journal(
                    trade.Id * 2,
                    trade,
                    WalletEntryType.MarketTransaction,
                    trade.WalletAmountCents ?? (trade.IsBuy ? -gross : gross),
                    trade.BalanceCents + trade.TaxCents
                )
            );
        }
        foreach (var payment in journal.ServicePayments ?? [])
        {
            entries.Add(
                new WalletJournalEntry(
                    EconomicRules.FirstPaymentJournalId + payment.Id,
                    payment.Time,
                    payment.Kind,
                    payment.PilotId,
                    payment.CounterpartyId,
                    payment.ContextId,
                    payment.AmountCents,
                    payment.BalanceCents
                )
            );
        }
        foreach (var payment in journal.MarketPayments ?? [])
        {
            entries.Add(
                new WalletJournalEntry(
                    PersonalMarketRules.FirstPaymentJournalId + payment.Id,
                    payment.Time,
                    payment.Kind,
                    payment.AmountCents < 0 ? payment.PilotId : payment.CounterpartyId,
                    payment.AmountCents < 0 ? payment.CounterpartyId : payment.PilotId,
                    payment.OrderId,
                    payment.AmountCents,
                    payment.BalanceCents
                )
            );
        }
        foreach (var reward in journal.Rewards)
        {
            if (!filter.Includes(reward.Time, clock.GetUtcNow().UtcDateTime))
            {
                continue;
            }
            entries.Add(
                new(
                    CombatDefinitions.FirstBountyJournalId + reward.Id,
                    reward.Time,
                    WalletEntryType.BountyPrizes,
                    CombatDefinitions.ConcordCorporation,
                    reward.PilotId,
                    reward.SystemId,
                    reward.AmountCents,
                    reward.BalanceCents + reward.TaxCents
                )
            );
        }
        foreach (var payment in journal.Repairs)
        {
            if (!filter.Includes(payment.Time, clock.GetUtcNow().UtcDateTime))
            {
                continue;
            }
            entries.Add(
                new(
                    RepairDefinitions.FirstRepairJournalId + payment.Id,
                    payment.Time,
                    WalletEntryType.RepairBill,
                    payment.PilotId,
                    payment.StationOwnerId,
                    payment.StationId,
                    -payment.AmountCents,
                    payment.BalanceCents
                )
            );
        }
        foreach (var grant in journal.Grants)
        {
            if (filter.Includes(grant.Time, clock.GetUtcNow().UtcDateTime))
            {
                entries.Add(
                    new(
                        TestingMarketRules.FirstGrantJournalId + grant.PilotId,
                        grant.Time,
                        WalletEntryType.GmCashTransfer,
                        MarketDefinitions.SccCorporationId,
                        grant.PilotId,
                        grant.PilotId,
                        grant.AmountCents,
                        grant.BalanceCents
                    )
                );
            }
        }
        foreach (var entry in journal.Insurance)
        {
            if (filter.Includes(entry.Time, clock.GetUtcNow().UtcDateTime))
            {
                entries.Add(
                    new(
                        InsuranceRules.FirstJournalId + entry.Id,
                        entry.Time,
                        WalletEntryType.Insurance,
                        entry.AmountCents > 0 ? InsuranceRules.InsurerCorporationId : entry.PilotId,
                        entry.AmountCents > 0 ? entry.PilotId : InsuranceRules.InsurerCorporationId,
                        // Build 3662168 FormatReference uses positive type IDs for
                        // loss payouts and negative ship IDs for policy purchases.
                        entry.AmountCents > 0
                            ? entry.TypeId
                            : -entry.ShipId,
                        entry.AmountCents,
                        entry.BalanceCents
                    )
                );
            }
        }
        foreach (var payment in journal.CorporationPayments ?? [])
        {
            if (filter.Includes(payment.Time, clock.GetUtcNow().UtcDateTime))
            {
                entries.Add(
                    new(
                        CorporationRules.FirstJournalId + payment.Id,
                        payment.Time,
                        WalletEntryType.CorporationRegistration,
                        payment.PilotId,
                        MarketDefinitions.SccCorporationId,
                        payment.CorporationId,
                        -CorporationRules.CreationFeeCents,
                        payment.BalanceCents
                    )
                );
            }
        }
        foreach (var payment in journal.HomeStationPayments)
        {
            if (filter.Includes(payment.Time, clock.GetUtcNow().UtcDateTime))
            {
                entries.Add(
                    new(
                        HomeStationRules.FirstJournalId + payment.Id,
                        payment.Time,
                        WalletEntryType.CloneTransfer,
                        payment.PilotId,
                        payment.OperatorId,
                        payment.StationId,
                        -payment.AmountCents,
                        payment.BalanceCents
                    )
                );
            }
        }
        foreach (var payment in journal.JumpClonePayments)
        {
            if (filter.Includes(payment.Time, clock.GetUtcNow().UtcDateTime))
            {
                entries.Add(
                    new(
                        JumpCloneRules.FirstJournalId + payment.Id,
                        payment.Time,
                        payment.Kind == JumpClonePaymentKind.Installation
                            ? WalletEntryType.JumpCloneInstallation
                            : WalletEntryType.JumpCloneActivation,
                        payment.PilotId,
                        payment.OperatorId,
                        payment.StationId,
                        -payment.AmountCents,
                        payment.BalanceCents
                    )
                );
            }
        }
        foreach (var payment in journal.Contracts)
        {
            if (filter.Includes(payment.Time, clock.GetUtcNow().UtcDateTime))
            {
                entries.Add(
                    new(
                        ContractRules.FirstJournalId + payment.Id,
                        payment.Time,
                        payment.Type,
                        payment.AmountCents < 0 ? payment.PilotId : payment.CounterpartyId,
                        payment.AmountCents < 0 ? payment.CounterpartyId : payment.PilotId,
                        payment.ContractId,
                        payment.AmountCents,
                        payment.BalanceCents
                    )
                );
            }
        }
        foreach (var reward in journal.RedeemedCurrencies ?? [])
        {
            if (filter.Includes(reward.Time, clock.GetUtcNow().UtcDateTime))
            {
                entries.Add(
                    new(
                        CurrencyRewardRules.FirstJournalId + reward.TokenId,
                        reward.Time,
                        WalletEntryType.RedeemedIskToken,
                        MarketDefinitions.SccCorporationId,
                        reward.PilotId,
                        reward.TokenId,
                        reward.Amount,
                        reward.After
                    )
                );
            }
        }
        entries.AddRange(
            corporationWallet
                .Journal(context.Pilot.PilotId, false, (int)WalletAccountKey.Cash, filter.Period(clock.GetUtcNow().UtcDateTime))
                .Where(entry => filter.Includes(entry.Time, clock.GetUtcNow().UtcDateTime))
                .Select(entry => new WalletJournalEntry(
                    CorporationWalletRules.FirstJournalId + entry.Id,
                    entry.Time,
                    (WalletEntryType)entry.EntryType,
                    entry.AmountCents < 0 ? entry.OwnerId : entry.CounterpartyId,
                    entry.AmountCents < 0 ? entry.CounterpartyId : entry.OwnerId,
                    entry.ReferenceId,
                    entry.AmountCents,
                    entry.BalanceCents,
                    entry.Reason
                ))
        );
        return new BlueList<WalletJournalEntry>(entries.OrderByDescending(entry => entry.Time).ToArray());
    }

    private static WalletJournalEntry Journal(long id, MarketTrade trade, WalletEntryType type, long amount, long balance) =>
        new(
            id,
            trade.Time,
            type,
            type == WalletEntryType.TransactionTax || trade.IsBuy ? trade.PilotId : trade.CounterpartyId,
            type == WalletEntryType.TransactionTax ? MarketDefinitions.SccCorporationId
                : trade.IsBuy ? trade.CounterpartyId
                : trade.PilotId,
            trade.TypeId,
            amount,
            balance
        );

    private PilotCallResult ReadCorporation(PilotCallContext context, string method, object?[] args, Dictionary<string, object?>? keywords)
    {
        try
        {
            if (method == "GetCashBalance")
            {
                if (keywords is not null && keywords.Keys.Any(key => key != "accountKey"))
                {
                    throw new InvalidDataException("Unexpected corporation balance arguments.");
                }
                int key = keywords?.GetValueOrDefault("accountKey") switch
                {
                    null => context.Pilot.Identity.CorporationAccountKey,
                    long number when number is >= CorporationWalletRules.MasterKey and <= CorporationWalletRules.LastKey => (int)number,
                    _ => throw new InvalidDataException("Choose a corporation wallet division."),
                };
                return Isk(corporationWallet.Balance(context.Pilot.PilotId, key));
            }
            if (
                keywords is { Count: > 0 }
                || args is not [long account, var year, var month, true]
                || account is < CorporationWalletRules.MasterKey or > CorporationWalletRules.LastKey
                || !(year is null && month is null)
                    && !(year is long y && y is >= 1601 and <= 9999 && month is long m && m is >= 1 and <= 12)
            )
            {
                throw new InvalidDataException("Choose a valid corporation wallet division and journal date.");
            }
            WalletJournalFilter filter = new(year is long yr ? (int)yr : null, month is long mo ? (int)mo : null);
            return new BlueList<WalletJournalEntry>(
                corporationWallet
                    .Journal(context.Pilot.PilotId, true, (int)account, filter.Period(clock.GetUtcNow().UtcDateTime))
                    .Where(e => filter.Includes(e.Time, clock.GetUtcNow().UtcDateTime))
                    .Select(e => new WalletJournalEntry(
                        CorporationWalletRules.FirstJournalId + e.Id,
                        e.Time,
                        e.EntryType,
                        e.AmountCents < 0 ? e.OwnerId : e.CounterpartyId,
                        e.AmountCents < 0 ? e.CounterpartyId : e.OwnerId,
                        e.ReferenceId,
                        e.AmountCents,
                        e.BalanceCents,
                        e.Reason
                    ))
                    .ToArray()
            );
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
