using Eden.Gameplay.Propulsion;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<long, SpatialStatus> _spatialNavigation = new(
        "resident.spatialNavigation",
        "WorldSpatialNavigation",
        "WorldActiveModule",
        "WorldItemMembership",
        "WorldShip",
        "WorldPilot"
    );

    internal SpatialStatus SpatialNavigation(long ship, Func<SpatialStatus> load) => Read(_spatialNavigation, ship, load);

    private readonly ResidentSnapshot<(long Pilot, long Ship, long Module), bool> _spatialMembership = new(
        "resident.spatialMembership",
        "WorldItemMembership",
        "WorldPilot",
        "WorldShip"
    );

    internal bool SpatialMembership(long pilot, long ship, long module, Func<bool> load) =>
        Read(_spatialMembership, (pilot, ship, module), load);
}
