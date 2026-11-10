namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerPauseRules
{
    public static MinerProgress Resume(MinerProgress work, long delay) =>
        work with
        {
            ActiveTicks = checked(work.ActiveTicks + delay),
            ClockOffsetTicks = checked(work.ClockOffsetTicks + delay),
            DueTicks = work.DueTicks == 0 ? 0 : checked(work.DueTicks + delay),
        };

    public static HaulerProgress Resume(HaulerProgress work, long delay, long now)
    {
        long wallDelay = work.Ratting is { LastObservedAt: > 0 } activity ? Math.Max(0, now - activity.LastObservedAt) : 0;
        var defense = work.Defense;
        if (wallDelay > 0 && defense is not null)
        {
            defense = defense with
            {
                Locks = defense.Locks.Select(scan => scan with { ReadyAt = checked(scan.ReadyAt + wallDelay) }).ToArray(),
                Cycles = defense
                    .Cycles.Select(cycle =>
                        cycle with
                        {
                            StartedAt = checked(cycle.StartedAt + wallDelay),
                            ReadyAt = checked(cycle.ReadyAt + wallDelay),
                        }
                    )
                    .ToArray(),
                RecallUntil = defense.RecallUntil == 0 ? 0 : checked(defense.RecallUntil + wallDelay),
            };
        }
        return work with
        {
            Defense = defense,
            Ratting = work.Ratting is { } ratting ? ratting with { LastObservedAt = now } : null,
            ActiveTicks = checked(work.ActiveTicks + delay),
            ClockOffsetTicks = checked(work.ClockOffsetTicks + delay),
            DueTicks = work.DueTicks == 0 ? 0 : checked(work.DueTicks + delay),
        };
    }
}
