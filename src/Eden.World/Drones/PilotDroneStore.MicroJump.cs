using Eden.Data.World.Drones;
using Eden.Gameplay.Drones;
using Eden.World.Combat;
using Eden.World.Runtime.Economy;

namespace Eden.World.Drones;

internal sealed partial class PilotDroneStore
{
    internal static bool FighterJumpDisrupted(WorldAction world, long fighter, long now) =>
        PlayerEwarStore.Active(world, fighter).Any(effect => effect.ExpiresAt > now && effect.Applied?.WarpStrength > 0);

    private static void CancelFighterMicroJump(WorldDrone drone)
    {
        var work = ReadWork(drone);
        if (work.FighterMobility is { Destination: not null } cycle)
        {
            WriteWork(drone, work with { FighterMobility = cycle with { StartedAt = 0, Destination = null } });
        }
    }

    private static void CompleteFighterMicroJump(WorldAction world, WorldDrone drone, long now)
    {
        var work = ReadWork(drone);
        if (work.FighterMobility is not { Destination: { } point } cycle || cycle.EndsAt > now)
        {
            return;
        }
        if (drone.Activity != DroneActivity.ReturningToBay && !FighterJumpDisrupted(world, drone.ItemId, now))
        {
            (drone.X, drone.Y, drone.Z) = (point.X, point.Y, point.Z);
            (drone.VelocityX, drone.VelocityY, drone.VelocityZ) = (0, 0, 0);
            drone.UpdatedAt = now;
            drone.Orbiting = false;
            cycle = cycle with { JumpedAt = now };
        }
        WriteWork(drone, work with { FighterMobility = cycle with { Destination = null } });
    }
}
