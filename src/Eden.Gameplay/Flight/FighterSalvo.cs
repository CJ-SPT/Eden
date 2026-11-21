using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Propulsion;

namespace Eden.Gameplay.Flight;

public sealed record FighterSalvo(
    MissileWarhead Warhead,
    double Range,
    double DurationMilliseconds,
    int Charges,
    double RearmMilliseconds,
    DogmaAttributeId? TargetResistance
);

public sealed record FighterSalvoCycle(int Charges, int Maximum, long RearmTicks, long StartedAt = 0, long EndsAt = 0, long RearmAt = 0)
{
    public static FighterSalvoCycle Full(int charges, double rearmMilliseconds) =>
        new(charges, charges, (long)Math.Ceiling(rearmMilliseconds * TimeSpan.TicksPerMillisecond));

    public bool Active(long now) => StartedAt > 0 && EndsAt > now;

    public FighterSalvoCycle Refresh(long now)
    {
        if (RearmAt == 0 || RearmAt > now)
        {
            return this;
        }
        int restored = (int)Math.Min(Maximum - Charges, (now - RearmAt) / RearmTicks + 1);
        int available = Charges + restored;
        return this with { Charges = available, RearmAt = available == Maximum ? 0 : checked(RearmAt + restored * RearmTicks) };
    }

    public FighterSalvoCycle Fire(long now, double duration)
    {
        long end = SpatialModuleRules.Deadline(now, duration);
        return this with { Charges = Charges - 1, StartedAt = now, EndsAt = end, RearmAt = checked(end + RearmTicks) };
    }
}
