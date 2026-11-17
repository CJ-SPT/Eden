using Eden.Data.World.Propulsion;
using Eden.Gameplay.Flight;
using Eden.World.Runtime.Economy;

namespace Eden.World.Propulsion;

internal static class GateCloakPersistence
{
    internal static void Cancel(WorldAction world, long ship)
    {
        if (world.Set<SpatialNavigationState>().Find(ship) is { GateCloakUntil: not 0 } state)
        {
            state.GateCloakUntil = 0;
        }
    }

    internal static void Start(WorldAction world, long ship, long now)
    {
        var state = world.Set<SpatialNavigationState>().Find(ship) ?? world.Add(new SpatialNavigationState { ShipId = ship }).Entity;
        state.GateCloakUntil = checked(now + GateTravelRules.CloakDurationTicks);
    }
}
