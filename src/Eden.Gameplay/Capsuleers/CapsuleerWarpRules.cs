using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Capsuleers;

public sealed record CapsuleerWarpOrder(DockedPilot Pilot, StaticSpaceObject Target, int Stamp, double ApproachRange, double StandOff = 0);

public static class CapsuleerWarpRules
{
    public const int MinimumDelaySeconds = 4;
    public const int DelayVariants = 5;

    public static long Delay(long pilot) =>
        TimeSpan.FromSeconds(MinimumDelaySeconds + TravelerRoutineRules.Mix((ulong)pilot) % DelayVariants).Ticks;

    public static long Begin(NavigationState navigation, CapsuleerWarpOrder order, long elapsed)
    {
        var pilot = order.Pilot;
        navigation.CancelAlignment();
        bool distant = navigation.StartWarp(pilot.ShipId, pilot.Flight, pilot.Position!, order.Target, order.StandOff, order.Stamp);
        navigation.CancelAlignment();
        navigation.Follow(
            pilot.ShipId,
            order.Stamp,
            pilot.Position!,
            pilot.Flight.ShipRadius,
            order.Target,
            order.ApproachRange,
            false,
            true
        );
        return distant ? checked(elapsed + Delay(pilot.PilotId)) : 0;
    }

    public static long Advance(NavigationState navigation, CapsuleerWarpOrder order, long readyAt, long elapsed, bool blocked)
    {
        if (navigation.Warp is not null)
        {
            return 0;
        }
        if (readyAt == 0 && navigation.WarpDestination is not null)
        {
            return Begin(navigation, order, elapsed);
        }
        if (readyAt > 0 && elapsed >= readyAt && !blocked && navigation.WarpDestination is null)
        {
            var pilot = order.Pilot;
            navigation.StartWarp(pilot.ShipId, pilot.Flight, pilot.Position!, order.Target, order.StandOff, order.Stamp);
        }
        if (blocked || elapsed < readyAt)
        {
            navigation.CancelAlignment();
        }
        return readyAt;
    }
}
