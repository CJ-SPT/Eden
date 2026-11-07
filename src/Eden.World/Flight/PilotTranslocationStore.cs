using Eden.Data.Catalog;
using Eden.Data.World.Docking;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Flight;

[Injectable]
internal sealed class PilotTranslocationStore(WorldDatabase database, SqliteWorld lookup, ISystemObjects objects) : IPilotTranslocation
{
    // Build 3662168 inventorycommon.const.groupSun.
    private const int StarGroup = 6;

    public TranslocationTarget Resolve(int targetId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (catalog.Set<Station>().Find(targetId) is { } station)
        {
            return new(targetId, SystemObjects.FindSystem(catalog, station.SolarSystemId), station.Id, SpacePosition.Zero, 0);
        }
        if (catalog.Set<Celestial>().Find(targetId) is { } body)
        {
            var type = catalog.Set<ItemType>().Find(body.TypeId) ?? throw new InvalidDataException("Unknown destination type.");
            return new(
                targetId,
                SystemObjects.FindSystem(catalog, body.SolarSystemId),
                null,
                new(body.X, body.Y, body.Z),
                body.Radius ?? type.Radius ?? 0
            );
        }
        if (catalog.Set<SolarSystem>().Find(targetId) is not null)
        {
            var star = objects.Load(targetId).FirstOrDefault(body => body.GroupId == StarGroup);
            return new(
                targetId,
                SystemObjects.FindSystem(catalog, targetId),
                null,
                star?.Position ?? SpacePosition.Zero,
                star?.Radius ?? 0
            );
        }
        throw new InvalidDataException("Choose a known solar system, NPC station or static celestial destination.");
    }

    public PilotTranslocation Move(long pilotId, long expectedShipId, int targetId)
    {
        lock (database.MotionLock)
        {
            // Loading a system can initialize its asteroid rows. Do that before
            // acquiring the move transaction's SQLite writer on this same world.
            var target = Resolve(targetId);
            var destinationObjects = target.StationId is null ? objects.Load(target.System.Id) : [];
            using WorldDbContext world = new(database.Path);
            using var transaction = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(database.CatalogPath);
            var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilotId);
            if (
                pilot is null
                || pilot.ActiveShipId != expectedShipId
                || pilot.ActiveShip.OwnerId != pilotId
                || pilot.ActiveShip.StoredInId is not null
            )
            {
                throw new InvalidDataException("Translocate only your current active ship.");
            }
            var ship = pilot.ActiveShip;
            var previous = PilotFleetStore.Read(catalog, ship);
            if (target.StationId is int stationId)
            {
                ship.StationId = stationId;
                if (ship.Position is { } saved)
                {
                    world.Positions.Remove(saved);
                    ship.Position = null;
                }
            }
            else
            {
                double radius = catalog.Set<ItemType>().Single(t => t.Id == ship.TypeId).Radius ?? 0;
                var point = TranslocationRules.Arrival(target, radius, destinationObjects);
                ship.Position ??= world.Positions.Add(new FlightPosition { ShipId = ship.Id }).Entity;
                ship.Position.SystemId = target.System.Id;
                (ship.Position.X, ship.Position.Y, ship.Position.Z) = (point.X, point.Y, point.Z);
            }
            world.SaveChanges();
            var current = PilotFleetStore.Read(catalog, ship);
            transaction.Commit();
            return new(lookup.FindPilot(pilot.AccountId, pilotId)!, new(current, previous));
        }
    }
}
