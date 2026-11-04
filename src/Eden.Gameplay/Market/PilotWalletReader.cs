using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Repair;

namespace Eden.Gameplay.Market;

public sealed record WalletGrant(long PilotId, long AmountCents, long BalanceCents, long Time);

public sealed record WalletJournal(
    IReadOnlyList<MarketTrade> Trades,
    IReadOnlyList<CombatReward> Rewards,
    IReadOnlyList<RepairPayment> Repairs,
    IReadOnlyList<WalletGrant> Grants,
    IReadOnlyList<InsuranceLedgerEntry> Insurance,
    IReadOnlyList<HomeStationPayment> HomeStationPayments,
    IReadOnlyList<JumpClonePayment> JumpClonePayments
);

public interface IPilotWalletReader
{
    long Balance(long pilotId);
    IReadOnlyList<WalletEntryDefinition> EntryTypes();
    WalletJournal Journal(long pilotId);
}
