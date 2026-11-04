using Eden.Gameplay.Combat;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Repair;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

[Injectable]
internal sealed class WalletHandler(IPilotWalletReader wallet, TimeProvider clock) : ISelectedPilotCallHandler
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
        var journal = wallet.Journal(context.Pilot.PilotId);
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
                    trade.IsBuy ? -gross : gross,
                    trade.BalanceCents + trade.TaxCents
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
                    reward.BalanceCents
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
}
