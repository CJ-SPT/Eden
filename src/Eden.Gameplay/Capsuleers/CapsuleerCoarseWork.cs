using Eden.Gameplay.Mining;

namespace Eden.Gameplay.Capsuleers;

public sealed record CapsuleerCoarseWork(int SystemId, long Generation, long Cursor, long ThroughTicks, long NextTicks)
{
    public IReadOnlyList<int> Systems { get; init; } = [];
    public long SettledTicks { get; init; } = ThroughTicks;
}

public sealed record CapsuleerCoarseResult(CapsuleerJob Job, CapsuleerCoarseWork Work);

public sealed record CapsuleerResumeWork(long EntryTicks, long SettledTicks, long PreviousDueTicks, int SystemId);

public sealed record CapsuleerCoarseMiningInputs(
    long AvailableTicks,
    MiningLaser Laser,
    double Capacitor,
    double AvailableVolume,
    long RemainingUnits,
    double OreVolume,
    bool Crystal
);

public static class CapsuleerCoarseRules
{
    public const int PageSize = 16;
    public const int MaximumTransitions = 32;
    public const int IntervalSeconds = 60;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(IntervalSeconds);
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(5);
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(50);

    public static long FirstDue(int system, long elapsed) =>
        checked(elapsed + TimeSpan.FromSeconds(1 + (uint)system % IntervalSeconds).Ticks);

    public static long NextDue(long elapsed) => checked(elapsed + Interval.Ticks);

    public static int MiningCycles(CapsuleerCoarseMiningInputs inputs)
    {
        var (availableTicks, laser, capacitor, availableVolume, remainingUnits, oreVolume, crystal) = inputs;
        double durationMilliseconds = laser.DurationMilliseconds;
        double cost = laser.CapacitorNeed;
        if (
            availableTicks <= 0
            || !double.IsFinite(durationMilliseconds)
            || durationMilliseconds <= 0
            || !double.IsFinite(capacitor)
            || capacitor < 0
            || !double.IsFinite(cost)
            || cost < 0
            || !double.IsFinite(availableVolume)
            || availableVolume < 0
            || !double.IsFinite(laser.Volume)
            || laser.Volume <= 0
            || !double.IsFinite(oreVolume)
            || oreVolume <= 0
            || availableVolume < oreVolume
            || remainingUnits <= 0
        )
        {
            return 0;
        }
        double timed = Math.Floor(availableTicks / (durationMilliseconds * TimeSpan.TicksPerMillisecond));
        double affordable = cost == 0 ? int.MaxValue : Math.Floor(capacitor / cost);
        double material = Math.Ceiling(Math.Min(availableVolume, remainingUnits * oreVolume) / laser.Volume);
        int cycles = (int)Math.Min(int.MaxValue, Math.Min(material, Math.Min(timed, affordable)));
        return crystal ? Math.Min(1, cycles) : cycles;
    }
}
