using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Flight;

public interface IPilotGateTravel
{
    DockedPilot Transfer(long pilotId, long shipId, long sourceGateId, long exitGateId);
}

public static class GateTravelRules
{
    public const double ActivationRange = 2500;
    public const int DurationTicks = 10;
    public const int ArrivalTicks = 8;
    private const double ArrivalClearance = 12000;

    public static bool InRange(SpacePosition position, double shipRadius, StaticSpaceObject gate) =>
        position.DistanceTo(gate.Position) <= gate.Radius + shipRadius + ActivationRange;

    public static SpacePosition Arrival(long shipId, SpacePosition exit, double gateRadius, double shipRadius) =>
        exit + new SpacePosition((shipId % 2 == 0 ? -1 : 1) * (gateRadius + shipRadius + ArrivalClearance), 0, 0);
}
