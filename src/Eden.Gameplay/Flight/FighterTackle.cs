namespace Eden.Gameplay.Flight;

public sealed record FighterTackle(double Range, double DurationMilliseconds, double WebPercent, double WarpStrength);

public sealed record FighterTackleCycle(long TargetId, long StartedAt, long EndsAt)
{
    public bool Active(long now) => StartedAt > 0 && EndsAt > now;
}
