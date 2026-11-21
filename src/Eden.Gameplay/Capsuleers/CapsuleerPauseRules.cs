using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerPauseRules
{
    public static MinerProgress Resume(MinerProgress work, long delay) =>
        work with
        {
            ActiveTicks = checked(work.ActiveTicks + delay),
            ClockOffsetTicks = checked(work.ClockOffsetTicks + delay),
            DueTicks = work.DueTicks == 0 ? 0 : checked(work.DueTicks + delay),
            WarpReadyAt = work.WarpReadyAt == 0 ? 0 : checked(work.WarpReadyAt + delay),
        };

    public static HaulerProgress Resume(HaulerProgress work, long delay, long now)
    {
        work = ExpireArrival(work, now);
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
                Budgets = defense
                    .Budgets.Select(budget =>
                        budget with
                        {
                            AmmoCheckAt = budget.AmmoCheckAt == 0 ? 0 : checked(budget.AmmoCheckAt + wallDelay),
                        }
                    )
                    .ToArray(),
            };
        }
        return work with
        {
            Defense = defense,
            Exploration = work.Exploration is { } exploring
                ? exploring with
                {
                    EndsAt = checked(exploring.EndsAt + delay),
                    ReadyTicks = exploring.ReadyTicks == 0 ? 0 : checked(exploring.ReadyTicks + delay),
                }
                : null,
            ExplorationRetryAt = work.ExplorationRetryAt == 0 ? 0 : checked(work.ExplorationRetryAt + delay),
            WarpReadyAt = work.WarpReadyAt == 0 ? 0 : checked(work.WarpReadyAt + delay),
            SovereigntyExpedition = work.SovereigntyExpedition is { LockReadyAt: > 0 } expedition
                ? expedition with
                {
                    LockReadyAt = checked(expedition.LockReadyAt + delay),
                }
                : work.SovereigntyExpedition,
            Ratting = work.Ratting is { } ratting
                ? ratting with
                {
                    LastObservedAt = now,
                    Salvage = ratting.Salvage is { } salvage ? salvage with { DueTicks = checked(salvage.DueTicks + delay) } : null,
                    Patrol = ratting.Patrol is { } patrol
                        ? patrol with
                        {
                            NextLook = patrol.NextLook == 0 ? 0 : checked(patrol.NextLook + delay),
                            LeaveGridAt = patrol.LeaveGridAt == 0 ? 0 : checked(patrol.LeaveGridAt + delay),
                        }
                        : null,
                }
                : null,
            ActiveTicks = checked(work.ActiveTicks + delay),
            ClockOffsetTicks = checked(work.ClockOffsetTicks + delay),
            DueTicks = work.DueTicks == 0 ? 0 : checked(work.DueTicks + delay),
        };
    }

    // Preserve the transfer identity without replaying its expired presentation.
    public static HaulerProgress ExpireArrival(HaulerProgress work, long now) =>
        work.ArrivalUntil > work.Stamp
        && work.ArrivalAt is { } arrived
        && now - arrived >= TimeSpan.FromSeconds(GateTravelRules.ArrivalTicks).Ticks
            ? work with
            {
                ArrivalUntil = work.Stamp,
            }
            : work;
}
