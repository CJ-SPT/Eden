using Eden.Gameplay.Thermal;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Storage;

internal static partial class WorldConnections
{
    internal static void PublishHeat(WorldAction world, long pilot, long ship, ShipHeatSnapshot state) =>
        LiveWorld.Find(world.WorldPath)?.PublishHeat(pilot, ship, state);
}
