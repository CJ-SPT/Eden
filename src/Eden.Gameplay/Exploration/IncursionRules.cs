namespace Eden.Gameplay.Exploration;

public static class IncursionRules
{
    public const int VanguardRewardGroup = 1;
    public const int ClearsToRepel = 8;
    public const int MaximumPaidFleetSize = 20;

    // Small-world campaign policy. UTC windows make placement and expiration independent of server uptime.
    public const long ActiveTicks = 3 * TimeSpan.TicksPerDay;
    public const long RotationTicks = 4 * TimeSpan.TicksPerDay;
    private static readonly long s_epoch = DateTime.UnixEpoch.ToFileTimeUtc();

    public static long TaleId(long now) => Math.Max(0, (now - s_epoch) / RotationTicks) + 1;

    public static long OpensAt(long taleId) => checked(s_epoch + (taleId - 1) * RotationTicks);

    public static long EndsAt(long taleId) => checked(OpensAt(taleId) + ActiveTicks);

    public static double Influence(int clears) => 1 - (double)Math.Clamp(clears, 0, ClearsToRepel) / ClearsToRepel;

    public static long Payout(int pilotCount, IReadOnlyList<long> rewards) =>
        pilotCount is > 0 and <= MaximumPaidFleetSize ? rewards[pilotCount] : 0;
}
