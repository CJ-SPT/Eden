using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Mining;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Flight;

internal sealed partial class PilotTranslocationStore
{
    private TranslocationTarget ResolveRuntime(CatalogDbContext catalog, long targetId)
    {
        if (targetId <= 0)
        {
            throw new InvalidDataException("Choose a positive destination ID.");
        }
        using WorldDbContext world = new(database.Path);
        // Project resident motion before reading, and resolve all rows from one
        // coherent world image. This is an occasional GM operation, not a monitor.
        using var transaction = world.Database.BeginTransaction();
        var ship = world.Ships.AsNoTracking().Include(s => s.Position).SingleOrDefault(s => s.Id == targetId);
        if (ship is null)
        {
            ship = world
                .Pilots.AsNoTracking()
                .Include(p => p.ActiveShip)
                    .ThenInclude(s => s.Position)
                .Where(p => p.Id == targetId)
                .SingleOrDefault()
                ?.ActiveShip;
        }
        if (ship is { StoredInId: null })
        {
            var station = catalog.RequireStation(ship.StationId);
            return ship.Position is { } point
                ? Space(ship.TypeId, point.SystemId ?? station.SolarSystemId, new(point.X, point.Y, point.Z))
                : new(targetId, SystemObjects.FindSystem(catalog, station.SolarSystemId), station.Id, SpacePosition.Zero, 0);
        }
        if (world.Set<WorldNpc>().AsNoTracking().SingleOrDefault(n => n.Id == targetId) is { DestroyedAt: null, RetiredAt: null } npc)
        {
            return Space(npc.TypeId, npc.SystemId, new(npc.X, npc.Y, npc.Z));
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (world.Set<WorldWreck>().AsNoTracking().SingleOrDefault(w => w.Id == targetId && w.ExpiresAt > now) is { } wreck)
        {
            return Space(wreck.TypeId, wreck.SystemId, new(wreck.X, wreck.Y, wreck.Z));
        }
        if (world.Set<WorldAsteroid>().AsNoTracking().SingleOrDefault(a => a.Id == targetId && a.Quantity > 0) is { } asteroid)
        {
            return new(
                targetId,
                SystemObjects.FindSystem(catalog, asteroid.SystemId),
                null,
                new(asteroid.X, asteroid.Y, asteroid.Z),
                asteroid.Radius
            );
        }
        if (
            world.Set<WorldDrone>().AsNoTracking().SingleOrDefault(d => d.ItemId == targetId) is { SystemId: int systemId } drone
            && world.Set<OwnedItem>().AsNoTracking().SingleOrDefault(i => i.Id == targetId && i.LocationId == systemId) is { } item
        )
        {
            return Space(item.TypeId, systemId, new(drone.X, drone.Y, drone.Z));
        }
        throw new InvalidDataException("Choose a known system, station, celestial, pilot, ship, NPC, wreck, asteroid or deployed drone.");

        TranslocationTarget Space(int typeId, int systemId, SpacePosition position) =>
            new(targetId, SystemObjects.FindSystem(catalog, systemId), null, position, catalog.RequireItem(typeId).Radius ?? 0);
    }
}
