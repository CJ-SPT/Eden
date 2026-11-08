using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Capsuleers;

public sealed record CapsuleerSchedule(long DueTicks, CapsuleerAction Action);

public sealed record CapsuleerBatch(CapsuleerTurn Turn, IReadOnlyList<MinerObservation> Miners, IReadOnlyList<HaulerObservation> Haulers)
{
    public long Revision { get; init; }
    public IReadOnlyList<SystemSimulationState> Systems { get; init; } = [];
}

public interface ICapsuleerScheduler
{
    CapsuleerBatch Run(int destinyStamp);
    IReadOnlyList<CapsuleerJob> Jobs();
    MinerObservation? Miner(long pilotId);
    HaulerObservation? Hauler(long pilotId);
}

public static class CapsuleerScheduleRules
{
    // The native NavigationState warp sampler caps exit speed at 100 m/s.
    public const double MaximumWarpExitSpeed = 100;

    // Subwarp collision and threatened work retain the shared one-second rules.
    // Background warps use the same analytic course at their exact exit boundary.
    public static CapsuleerSchedule Next(MinerProgress work, bool inSpace)
    {
        var action = work.Activity switch
        {
            MinerActivity.Docked => CapsuleerAction.Prepare,
            MinerActivity.Locking => CapsuleerAction.Lock,
            MinerActivity.Mining => CapsuleerAction.Collect,
            MinerActivity.Waiting => CapsuleerAction.Retry,
            _ => CapsuleerAction.Arrival,
        };
        return Next(work.ActiveTicks, work.DueTicks, action, !inSpace && work.Activity == MinerActivity.Waiting);
    }

    public static CapsuleerSchedule Next(HaulerProgress work, bool inSpace)
    {
        var action = work.Activity switch
        {
            HaulerActivity.Docked => CapsuleerAction.Trade,
            HaulerActivity.Jumping => CapsuleerAction.GateTransfer,
            HaulerActivity.Waiting => CapsuleerAction.Retry,
            _ => CapsuleerAction.Arrival,
        };
        return Next(work.ActiveTicks, work.DueTicks, action, !inSpace && work.Activity == HaulerActivity.Waiting);
    }

    public static long? WarpDeadline(NavigationFrame navigation, FlightProperties flight, long offsetTicks, long activeTicks)
    {
        if (navigation.Warp is not { } frame)
        {
            return null;
        }
        WarpCourse course = new(frame.Origin, frame.Destination, frame.StartStamp, flight, frame.EntrySpeed);
        double exitSpeed = Math.Min(flight.MaxSpeed / 2, MaximumWarpExitSpeed);
        double duration = course.DurationSeconds(exitSpeed);
        if (!double.IsFinite(duration))
        {
            return null;
        }
        // WarpCourse exits strictly below the exit speed, including an integer boundary.
        int exitStamp = checked((int)Math.Floor(frame.StartStamp + duration) + 1);
        while (!course.Sample(exitStamp, exitSpeed).Exited)
        {
            exitStamp = checked(exitStamp + 1);
        }
        return Math.Max(checked(activeTicks + 1), checked(offsetTicks + exitStamp * TimeSpan.TicksPerSecond));
    }

    public static int Stamp(long activeTicks, long offsetTicks) => checked((int)((activeTicks - offsetTicks) / TimeSpan.TicksPerSecond));

    public static long MigrateDeadline(long elapsedTicks, int stamp, int dueStamp) =>
        checked(elapsedTicks + Math.Max(0L, (long)dueStamp - stamp) * TimeSpan.TicksPerSecond);

    private static CapsuleerSchedule Next(long activeTicks, long dueTicks, CapsuleerAction action, bool sleeping)
    {
        long pulse = checked(activeTicks + TimeSpan.TicksPerSecond);
        long next = sleeping && dueTicks > activeTicks ? dueTicks : pulse;
        if (action is CapsuleerAction.Lock or CapsuleerAction.Collect or CapsuleerAction.GateTransfer && dueTicks > activeTicks)
        {
            next = Math.Min(next, dueTicks);
        }
        return new(next, action);
    }
}
