using Eden.Gameplay.Combat;
using Eden.Gameplay.Repair;

namespace Eden.Gameplay.Market;

public sealed record WalletJournal(
    IReadOnlyList<MarketTrade> Trades,
    IReadOnlyList<CombatReward> Rewards,
    IReadOnlyList<RepairPayment> Repairs
);

public interface IPilotWalletReader
{
    long Balance(long pilotId);
    IReadOnlyList<WalletEntryDefinition> EntryTypes();
    WalletJournal Journal(long pilotId);
}
