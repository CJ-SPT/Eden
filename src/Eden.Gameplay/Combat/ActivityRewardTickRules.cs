namespace Eden.Gameplay.Combat;

public interface IActivityRewardTicks
{
    void Advance();
}

public static class ActivityRewardTickRules
{
    // Tranquility's NPC bounty window starts with the first bounty earned by each pilot.
    public const int DefaultIntervalSeconds = 20 * 60;
    public const int MaximumIntervalSeconds = 24 * 60 * 60;

    public static void ValidateInterval(int seconds)
    {
        if (seconds is < 0 or > MaximumIntervalSeconds)
        {
            throw new InvalidDataException($"Bounty tick interval must be between 0 and {MaximumIntervalSeconds} seconds.");
        }
    }

    public static long Deadline(long now, int intervalSeconds) => checked(now + intervalSeconds * TimeSpan.TicksPerSecond);
}
