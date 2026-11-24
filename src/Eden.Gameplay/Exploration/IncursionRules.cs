namespace Eden.Gameplay.Exploration;

public static class IncursionRules
{
    public const int ClearsToUnlockFinal = 8;

    // Eden's small-world rotation is independent of server uptime.
    public const long ActiveTicks = 3 * TimeSpan.TicksPerDay;
    public const long RotationTicks = 4 * TimeSpan.TicksPerDay;
    private static readonly long s_epoch = DateTime.UnixEpoch.ToFileTimeUtc();

    public static long TaleId(long now) => Math.Max(0, (now - s_epoch) / RotationTicks) + 1;

    public static long OpensAt(long taleId) => checked(s_epoch + (taleId - 1) * RotationTicks);

    public static long EndsAt(long taleId) => checked(OpensAt(taleId) + ActiveTicks);

    public static double Influence(int clears) => 1 - (double)Math.Clamp(clears, 0, ClearsToUnlockFinal) / ClearsToUnlockFinal;

    public static bool FinalAvailable(int clears, bool headquartersCleared) => clears >= ClearsToUnlockFinal && headquartersCleared;
}
