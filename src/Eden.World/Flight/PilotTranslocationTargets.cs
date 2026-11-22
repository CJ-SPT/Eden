using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Mining;
using Eden.Data.World.Structures;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;

namespace Eden.World.Flight;

internal sealed partial class PilotTranslocationStore
{
    private TranslocationTarget ResolveRuntime(CatalogDbContext catalog, long targetId)
    {
        if (targetId <= 0)
        {
            throw new InvalidDataException("Choose a positive destination ID.");
        }
        using WorldActionScope action = new(database, new(ships: [targetId], pilots: [targetId]));
        var world = action.World;
        var live = LiveWorld.Find(database.Path);
        if (world.Set<SavedStructure>().Find(targetId) is { State: not StructureState.Destroyed } structure)
        {
            return StructureTarget(structure, docked: false);
        }
        var ship = world.Ships.Find(targetId) ?? world.Filter<Pilot>(p => p.Id == targetId).SingleOrDefault()?.ActiveShip;
        if (ship is { StoredInId: null })
        {
            if (ship.Position is null && ship.StructureId is long structureId)
            {
                return StructureTarget(PilotStructureStore.Require(world, structureId), docked: true);
            }
            var station = catalog.RequireStation(ship.StationId);
            return ship.Position is { } point
                ? Space(ship.TypeId, point.SystemId ?? station.SolarSystemId, new(point.X, point.Y, point.Z))
                : new(targetId, SystemObjects.FindSystem(catalog, station.SolarSystemId), station.Id, SpacePosition.Zero, 0);
        }
        if (world.Set<WorldNpc>().Find(targetId) is { DestroyedAt: null, RetiredAt: null } npc)
        {
            return Space(npc.TypeId, npc.SystemId, live?.PublishedPosition(npc.SystemId, targetId) ?? new(npc.X, npc.Y, npc.Z));
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (world.Set<WorldWreck>().Find(targetId) is { } wreck && wreck.ExpiresAt > now)
        {
            return Space(wreck.TypeId, wreck.SystemId, live?.PublishedPosition(wreck.SystemId, targetId) ?? new(wreck.X, wreck.Y, wreck.Z));
        }
        if (world.Set<WorldAsteroid>().Find(targetId) is { Quantity: > 0 } asteroid)
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
            world.Set<WorldDrone>().Find(targetId) is { SystemId: int systemId } drone
            && world.Set<OwnedItem>().Find(targetId) is { } item
            && item.LocationId == systemId
        )
        {
            return Space(item.TypeId, systemId, live?.PublishedPosition(systemId, targetId) ?? new(drone.X, drone.Y, drone.Z));
        }
        throw new InvalidDataException("Choose a known system, station, structure, celestial, pilot, ship, NPC, wreck, asteroid or drone.");

        TranslocationTarget Space(int typeId, int systemId, SpacePosition position) =>
            new(targetId, SystemObjects.FindSystem(catalog, systemId), null, position, catalog.RequireItem(typeId).Radius ?? 0);

        TranslocationTarget StructureTarget(SavedStructure target, bool docked)
        {
            var profile = StructureCatalog.Require(catalog, target.TypeId);
            return new(
                targetId,
                SystemObjects.FindSystem(catalog, target.SystemId),
                null,
                new(target.X, target.Y, target.Z),
                profile.Radius
            )
            {
                StructureId = docked ? target.Id : null,
            };
        }
    }
}
