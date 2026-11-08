namespace Eden.Gameplay.Capsuleers;

public sealed record CapsuleerSchedule(long DueTicks, CapsuleerAction Action);

public sealed record CapsuleerBatch(CapsuleerTurn Turn, IReadOnlyList<MinerObservation> Miners, IReadOnlyList<HaulerObservation> Haulers);

public interface ICapsuleerScheduler
{
    CapsuleerBatch Run(int destinyStamp);
    IReadOnlyList<CapsuleerJob> Jobs();
    MinerObservation? Miner(long pilotId);
    HaulerObservation? Hauler(long pilotId);
}

public static class CapsuleerScheduleRules
{
    // Detailed navigation and combat still need one-second samples in this increment.
    // Docked retries can sleep until their actual deadline. Background event handoff
    // replaces these in-space samples in the following increment.
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
