namespace Eden.Gameplay.Sovereignty;

public enum SovereigntyActivityKind
{
    Military,
    Industrial,
}

public sealed record SovereigntyActivity(
    double Military,
    double Industrial,
    long UpdatedAt,
    long LastMilitaryAt = 0,
    long LastIndustrialAt = 0,
    long Revision = 0
);

public sealed record SovereigntyCapital(int? CurrentSystemId, int? NewSystemId, long? ValidAfter);

public sealed record SovereigntyDevelopment(
    int SystemId,
    double MilitaryPoints,
    double IndustrialPoints,
    SovereigntyIndices Indices,
    decimal DefenseMultiplier,
    bool IsCapital,
    bool MilitaryIncreasing,
    bool IndustrialIncreasing,
    long ClaimedDays,
    long ActivityRevision
);

public static class SovereigntyDevelopmentRules
{
    // Build 3662168 devIndexUtil: six bands, 100000 points/hour decay, 96 hours/band.
    public const double DecayPerHour = 100000;
    public const double LevelWidth = DecayPerHour * 96;
    public const double MaximumPoints = LevelWidth * 6;

    // Raw earning calibration is Eden policy; the client does not expose CCP's server formula.
    public const double MilitaryPointsPerKill = 10000;
    public static readonly long CapitalDelay = TimeSpan.FromDays(7).Ticks;
    public static readonly long TrendDuration = TimeSpan.TicksPerHour;
    private static readonly double[] s_militaryModifiers = [5, 3, 2, 1.5, 1, .8];
    private static readonly double[] s_industrialModifiers = [7.2, 2.36, 1.649, 1.2, .7, .35];

    public static int Level(double points)
    {
        if (!double.IsFinite(points) || points < 0)
        {
            throw new InvalidDataException("Invalid sovereignty activity points.");
        }
        return (int)Math.Min(SovereigntyRules.MaximumIndex, Math.Floor(points / LevelWidth));
    }

    public static SovereigntyActivity Decay(SovereigntyActivity state, long now)
    {
        _ = Level(state.Military);
        _ = Level(state.Industrial);
        if (now < 0 || state.UpdatedAt < 0)
        {
            throw new InvalidDataException("Invalid sovereignty activity timestamp.");
        }
        if (now <= state.UpdatedAt)
        {
            return state;
        }
        double loss = (now - state.UpdatedAt) / (double)TimeSpan.TicksPerHour * DecayPerHour;
        return state with
        {
            Military = Math.Max(0, state.Military - loss),
            Industrial = Math.Max(0, state.Industrial - loss),
            UpdatedAt = now,
        };
    }

    public static SovereigntyActivity Credit(SovereigntyActivity state, SovereigntyActivityKind kind, double amount, long now)
    {
        if (!double.IsFinite(amount) || amount < 0 || !Enum.IsDefined(kind))
        {
            throw new InvalidDataException("Invalid sovereignty contribution.");
        }
        var current = Decay(state, now);
        if (amount == 0)
        {
            return current;
        }
        double points = kind == SovereigntyActivityKind.Military ? current.Military : current.Industrial;
        double raw = kind == SovereigntyActivityKind.Military ? amount * MilitaryPointsPerKill : amount;
        var modifiers = kind == SovereigntyActivityKind.Military ? s_militaryModifiers : s_industrialModifiers;
        while (raw > 0 && points < MaximumPoints)
        {
            int level = Level(points);
            double limit = Math.Min(MaximumPoints, (level + 1) * LevelWidth);
            double consumed = Math.Min(raw, (limit - points) / modifiers[level]);
            points = Math.Min(limit, points + consumed * modifiers[level]);
            raw -= consumed;
        }
        long at = Math.Max(now, state.UpdatedAt);
        return kind == SovereigntyActivityKind.Military
            ? current with
            {
                Military = points,
                LastMilitaryAt = at,
            }
            : current with
            {
                Industrial = points,
                LastIndustrialAt = at,
            };
    }

    public static long NextLevelChange(SovereigntyActivity state, long after)
    {
        var current = Decay(state, after);
        long Next(double points)
        {
            int level = Level(points);
            if (level == 0)
            {
                return long.MaxValue;
            }
            double amount = points - level * LevelWidth;
            return checked(after + (long)Math.Floor(amount / DecayPerHour * TimeSpan.TicksPerHour) + 1);
        }
        return Math.Min(Next(current.Military), Next(current.Industrial));
    }
}
