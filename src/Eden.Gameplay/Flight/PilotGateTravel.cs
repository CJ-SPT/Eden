using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Flight;

public interface IPilotGateTravel
{
    DockedPilot Transfer(long pilotId, long shipId, long sourceGateId, long exitGateId);
}

public static class GateTravelRules
{
    public const double ActivationRange = 2500;
    public const int DurationTicks = 3;
    public const int ArrivalTicks = 8;
    private const double ArrivalClearance = 12000;

    public static bool InRange(SpacePosition position, double shipRadius, StaticSpaceObject gate) =>
        position.DistanceTo(gate.Position) <= gate.Radius + shipRadius + ActivationRange;

    public static SpacePosition Arrival(long shipId, SpacePosition exit, double gateRadius, double shipRadius)
    {
        // SplitMix64 spreads nearby ship IDs across a stable arrival shell. The previous
        // parity-based placement stacked every same-hull arrival onto two points.
        const ulong increment = 0x9E3779B97F4A7C15UL;
        const ulong firstMultiplier = 0xBF58476D1CE4E5B9UL;
        const ulong secondMultiplier = 0x94D049BB133111EBUL;
        const double uniformRange = 4294967296;
        ulong value = unchecked((ulong)shipId + increment);
        value = unchecked((value ^ (value >> 30)) * firstMultiplier);
        value = unchecked((value ^ (value >> 27)) * secondMultiplier);
        value ^= value >> 31;
        double azimuth = (uint)value * (Math.Tau / uniformRange);
        double height = (value >> 32) * (2 / uniformRange) - 1;
        double horizontal = Math.Sqrt(1 - height * height);
        var direction = new SpacePosition(horizontal * Math.Cos(azimuth), horizontal * Math.Sin(azimuth), height);
        return exit + direction * (gateRadius + shipRadius + ArrivalClearance);
    }
}
