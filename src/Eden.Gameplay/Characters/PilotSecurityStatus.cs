namespace Eden.Gameplay.Characters;

public enum SecuritySkill
{
    FastTalk = 3358,
}

public enum SecurityEvent
{
    // Build 3662168 appLogConst.eventSecStatusKillPirateNpc, read by the native history panel.
    PirateNpcKilled = 378,
}

public sealed record SecurityStatusView(double Value, long? PendingAt);

public sealed record SecurityReward(double Fraction, double Maximum);

public sealed record SecurityChange(double Before, double After, double Modification);

public sealed record SecurityStatusTransaction(
    long Id,
    long Time,
    SecurityEvent Event,
    long OtherOwnerId,
    int LocationId,
    double Modification,
    double NewValue,
    long NpcId,
    int TypeId
);

public interface IPilotSecurityStatus
{
    long Revision { get; }
    SecurityStatusView Read(long pilotId);
    IReadOnlyList<SecurityStatusTransaction> History(long pilotId);
}

public static class SecurityStatusRules
{
    public const double Minimum = -10;
    public const double Maximum = 10;
    public const int MaximumSkillLevel = 5;
    public const double Percent = 100;
    public static readonly TimeSpan RewardWindow = TimeSpan.FromMinutes(5);

    public static void Check(double value)
    {
        if (!double.IsFinite(value) || value < Minimum || value > Maximum)
        {
            throw new InvalidDataException("Invalid character security status.");
        }
    }

    public static SecurityReward Reward(double bonusPercent, double maximum, int level, double skillPercent)
    {
        Check(maximum);
        if (
            !double.IsFinite(bonusPercent)
            || bonusPercent < 0
            || bonusPercent > Percent
            || !double.IsFinite(skillPercent)
            || skillPercent < 0
            || skillPercent > Percent
            || level is < 0 or > MaximumSkillLevel
        )
        {
            throw new InvalidDataException("Invalid NPC security reward or Fast Talk bonus.");
        }
        return new(bonusPercent / Percent * (1 + level * skillPercent / Percent), maximum);
    }

    public static SecurityChange Apply(double current, SecurityReward reward)
    {
        Check(current);
        Check(reward.Maximum);
        if (!double.IsFinite(reward.Fraction) || reward.Fraction < 0)
        {
            throw new InvalidDataException("Invalid pending security reward.");
        }
        // Attribute 252 moves toward the upper security limit; attribute 563 caps the NPC's eligible result.
        double next = current >= reward.Maximum ? current : Math.Min(reward.Maximum, current + (Maximum - current) * reward.Fraction);
        return new(current, next, next == current ? 0 : (next - current) / (Maximum - current));
    }
}
