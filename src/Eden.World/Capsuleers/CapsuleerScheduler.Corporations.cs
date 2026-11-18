using Eden.Data.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    internal bool PrepareCorporations(int system, long afterPilot, out long cursor)
    {
        // Visit a bounded page even when the saved population fingerprint was already current on startup.
        const int batchSize = 6;
        cursor = afterPilot;
        if (Live is not { } live || !database.Settings.Capsuleers.Enabled)
        {
            return true;
        }
        using var coordinator = synchronization.TryEnterShared("capsuleer corporation placement");
        if (coordinator is null)
        {
            return false;
        }
        using var ownership = synchronization.TryEnterSystems("capsuleer corporation placement", [system]);
        if (ownership is null || !presence.Read().ActiveSystems.Contains(system))
        {
            return false;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        List<long> joined = [];
        long[] page;
        using (var world = live.Economy.Begin(new(systems: [system])))
        {
            page = world
                .Filter<SavedCapsuleerAgent>(row => row.CommunitySystemId == system)
                .Where(row => row.PilotId > afterPilot)
                .OrderBy(row => row.PilotId)
                .Select(row => row.PilotId)
                .Take(batchSize)
                .ToArray();
            foreach (long pilot in page)
            {
                var ship = world.Pilots.Find(pilot)!.ActiveShip;
                if (
                    world.Positions.Find(ship.Id) is null
                    && ship.StructureId is null
                    && catalog.RequireStation(ship.StationId).SolarSystemId == system
                    && CapsuleerCorporations.Place(world, catalog, database, pilot, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
                )
                {
                    joined.Add(pilot);
                }
            }
            if (joined.Count > 0)
            {
                world.Commit();
            }
        }
        foreach (long pilot in joined)
        {
            _miners.GetValueOrDefault(pilot)?.RefreshCorporation();
            _haulers.GetValueOrDefault(pilot)?.RefreshCorporation();
        }
        if (joined.Count > 0)
        {
            _presentations.Remove(system);
        }
        cursor = page.LastOrDefault(afterPilot);
        return page.Length < batchSize;
    }
}
