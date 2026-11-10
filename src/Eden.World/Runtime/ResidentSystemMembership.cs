using Eden.World.Storage;

namespace Eden.World.Runtime;

internal static class ResidentSystemMembership
{
    // Undocking in the native client keeps SystemId implicit until the first
    // inter-system trip. The station remains the authoritative fallback.
    internal static IQueryable<long> Ships(WorldDbContext world, WorldDatabase database, int system)
    {
        var stations = database.Catalog.StationsInSystem(system).Select(station => (long)station.Id).ToArray();
        return world
            .Ships.Where(ship =>
                ship.Position != null && ship.Position.SystemId == system
                || (ship.Position == null || ship.Position.SystemId == null) && stations.Contains(ship.StationId)
            )
            .Select(ship => ship.Id);
    }
}
