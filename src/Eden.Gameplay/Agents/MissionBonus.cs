namespace Eden.Gameplay.Agents;

public sealed record MissionBonus(long IskCents, long WindowTicks, long? Deadline)
{
    public long Remaining(long now) => Deadline is long deadline ? Math.Max(0, deadline - now) : WindowTicks;

    public long Payable(long now) => Deadline is not null && Remaining(now) > 0 ? IskCents : 0;
}

public static class MissionBonusRules
{
    // Eden policy: retain the agreed base reward and offer half again for a six-hour turn-in.
    // CCP's dynamically adjusted per-mission bonus quotes are not present in the catalog.
    public static readonly TimeSpan Window = TimeSpan.FromHours(6);
    private const int BaseRewardDivisor = 2;

    public static long Quote(long baseIskCents) => baseIskCents / BaseRewardDivisor;
}
