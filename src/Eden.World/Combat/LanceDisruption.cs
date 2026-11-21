using Eden.Data.World.Combat;
using Eden.World.Runtime.Economy;

namespace Eden.World.Combat;

internal static class LanceDisruption
{
    internal static bool Active(WorldAction world, long ship, long now) =>
        world is EconomyTransaction && world.Set<SavedLanceDisruption>().Find(ship)?.EndsAt > now;

    internal static void RequireTravel(WorldAction world, long ship, long now)
    {
        if (Active(world, ship, now))
        {
            throw new InvalidDataException("A disruptive lance prevents this ship from warping, docking or jumping.");
        }
    }
}
