namespace Eden.Gameplay.Exploration;

public static class IncursionRules
{
    public const int ClearsToUnlockFinal = 8;
    public const int HighSecurityLane = 0;
    public const int LowSecurityLane = 1;
    public const int FirstNullSecurityLane = 2;
    public const int NullSecurityLanes = 3;
    public const int CampaignLanes = FirstNullSecurityLane + NullSecurityLanes;

    // Preserve all existing high-security tale IDs. Each other rotation occupies a disjoint ID range.
    private const long TaleIdsPerLane = 1_000_000;
    public const long ActiveTicks = 3 * TimeSpan.TicksPerDay;
    public const long RotationTicks = 4 * TimeSpan.TicksPerDay;
    private static readonly long s_epoch = DateTime.UnixEpoch.ToFileTimeUtc();

    public static long TaleId(long now, int lane = HighSecurityLane) =>
        Math.Max(0, (now - s_epoch) / RotationTicks) + 1 + lane * TaleIdsPerLane;

    public static int Lane(long taleId) => checked((int)(taleId / TaleIdsPerLane));

    public static long Period(long taleId) => taleId % TaleIdsPerLane;

    public static long OpensAt(long taleId) => checked(s_epoch + (Period(taleId) - 1) * RotationTicks);

    public static long EndsAt(long taleId) => checked(OpensAt(taleId) + ActiveTicks);

    public static long FinalAgeAt(long taleId, IncursionSecurity security) =>
        security == IncursionSecurity.Low ? OpensAt(taleId) + TimeSpan.TicksPerDay : OpensAt(taleId);

    public static double Influence(int clears) => 1 - (double)Math.Clamp(clears, 0, ClearsToUnlockFinal) / ClearsToUnlockFinal;

    public static bool FinalAvailable(int clears, bool headquartersCleared) => clears >= ClearsToUnlockFinal && headquartersCleared;
}
