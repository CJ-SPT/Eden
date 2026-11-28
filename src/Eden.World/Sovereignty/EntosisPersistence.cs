using Eden.Data.World.Sovereignty;
using Eden.World.Runtime.Economy;

namespace Eden.World.Sovereignty;

internal static class EntosisPersistence
{
    internal static double? SpeedLimit(WorldAction world, long ship, long now) =>
        world.Filter<SavedEntosisCycle>(row => row.ShipId == ship && row.EndsAt > now).Select(row => (double?)row.SpeedLimit).Min();

    internal static long? TravelLockedUntil(WorldAction world, long ship, long now) =>
        world.Filter<SavedEntosisCycle>(row => row.ShipId == ship && row.EndsAt > now).Select(row => (long?)row.EndsAt).Max();

    internal static void RequireTravel(WorldAction world, long ship, long now)
    {
        if (world is EconomyTransaction && TravelLockedUntil(world, ship, now) is not null)
        {
            throw new InvalidDataException("Wait for the paid Entosis cycle to finish before travelling or cloaking.");
        }
    }

    internal static void RequireAssistance(WorldAction world, long ship, long now)
    {
        if (
            world is EconomyTransaction
            && world.Filter<SavedEntosisCycle>(row => row.ShipId == ship && row.Capital && row.EndsAt > now).Any()
        )
        {
            throw new InvalidDataException("A capital ship with an active Entosis Link cannot receive remote assistance.");
        }
    }
}
