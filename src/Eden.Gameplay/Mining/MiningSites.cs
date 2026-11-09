using Eden.Gameplay.Exploration;

namespace Eden.Gameplay.Mining;

public enum MiningFamily
{
    Ore,
    Ice,
    Gas,
}

public interface IMiningSites
{
    IReadOnlyList<CombatAnomaly> Signatures(int systemId, long now);
    IReadOnlyList<CombatAnomaly> Anomalies(int systemId, long now);
}

public static class MiningSiteRules
{
    public const int GasArchetype = 30;
    public const int IceArchetype = 28;
    public const int GasStrengthAttribute = 209;
    public const int DepositsPerSite = 1000;
    public const long FirstSiteId = 8_200_000_000_000;
    public const long FirstDepositId = 8_300_000_000_000;
    public const long DepositEndId = 8_400_000_000_000;
    public const long Lifetime = 3 * TimeSpan.TicksPerDay;
    public const long ReplacementDelay = 10 * TimeSpan.TicksPerMinute;
    public const long IceReplacementDelay = 6 * TimeSpan.TicksPerHour;

    public static bool IsDeposit(long id) => id >= FirstDepositId && id < DepositEndId;
}
