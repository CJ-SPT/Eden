using Eden.Data.World.Fittings;
using Eden.Gameplay.Propulsion;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<long, SpatialStatus> _spatialNavigation = new(
        "resident.spatialNavigation",
        "WorldSpatialNavigation",
        "WorldActiveModule",
        "WorldItemMembership",
        "WorldShip"
    );

    private void ObserveSpatialNavigation(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (!_spatialNavigation.HasEntries)
        {
            return;
        }
        var modules = changes.Where(c => c.Table is "WorldActiveModule" or "WorldItemMembership").Select(c => c.Id).ToArray();
        HashSet<long> currentShips = [];
        if (modules.Length > 0)
        {
            using WorldDbContext world = new(database.Path) { IsResidentProjection = true };
            currentShips = world.Set<ActiveFittedModule>().Where(m => modules.Contains(m.ModuleId)).Select(m => m.ShipId).ToHashSet();
        }
        _spatialNavigation.ObserveValues(
            changes,
            (ship, status, change) =>
                change.Table is "WorldShip" or "WorldSpatialNavigation"
                    ? ship == change.Id
                    : status.CloakModuleId == change.Id || currentShips.Contains(ship)
        );
    }

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
