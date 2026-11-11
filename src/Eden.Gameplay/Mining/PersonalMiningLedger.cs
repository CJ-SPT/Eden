namespace Eden.Gameplay.Mining;

public sealed record MiningLedgerQuantity(long Collected, long Critical, long Wasted);

public sealed record PersonalMiningLog(
    long Id,
    long PilotId,
    long Day,
    int CorporationId,
    int SystemId,
    int TypeId,
    MiningLedgerQuantity Quantity
);

public interface IPersonalMiningLedger
{
    IReadOnlyList<PersonalMiningLog> Read(long pilotId);
}

public static class MiningLedgerRules
{
    // Build 3662168 ledgerUtil.NUM_DAYS_BACK; ledger records are UTC daily totals.
    public const int RetentionDays = 90;

    public static long Day(long now)
    {
        if (now < 0)
        {
            throw new InvalidDataException("Use a nonnegative FILETIME for a mining ledger date.");
        }
        return now - now % TimeSpan.TicksPerDay;
    }

    public static long EarliestDay(long now) => Math.Max(0, Day(now) - (RetentionDays - 1L) * TimeSpan.TicksPerDay);

    public static MiningLedgerQuantity Add(MiningLedgerQuantity previous, MiningLedgerQuantity mined)
    {
        foreach (var quantity in new[] { previous, mined })
        {
            if (quantity.Collected < 0 || quantity.Critical < 0 || quantity.Wasted < 0 || quantity.Critical > quantity.Collected)
            {
                throw new InvalidDataException("Mining ledger quantities must be nonnegative; critical ore is part of collected ore.");
            }
        }
        return new(
            checked(previous.Collected + mined.Collected),
            checked(previous.Critical + mined.Critical),
            checked(previous.Wasted + mined.Wasted)
        );
    }
}
