namespace Eden.Gameplay.Capsuleers;

// Runtime retry eligibility never replaces a saved gameplay deadline or revision.
public sealed record CapsuleerRetry(int Attempts, long DueTicks)
{
    public const int MaximumAttempts = 6;
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);

    public static CapsuleerRetry Next(CapsuleerRetry? previous, long nowTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowTicks);
        int attempts = Math.Min(MaximumAttempts, (previous?.Attempts ?? 0) + 1);
        long delay = Math.Min(MaximumDelay.Ticks, InitialDelay.Ticks << (attempts - 1));
        return new(attempts, nowTicks > long.MaxValue - delay ? long.MaxValue : nowTicks + delay);
    }
}
