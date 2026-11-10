using Eden.Data.Catalog;
using Eden.Data.World.Docking;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Flight;

[Injectable]
internal sealed partial class PilotTranslocationStore(
    WorldDatabase database,
    SqliteWorld lookup,
    ISystemObjects objects,
    TimeProvider clock
) : IPilotTranslocation
{
    // Build 3662168 inventorycommon.const.groupSun.
    private const int StarGroup = 6;

    public TranslocationTarget Resolve(long targetId)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            return ResolveTarget(targetId);
        }
    }

    private TranslocationTarget ResolveTarget(long targetId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (targetId is > 0 and <= int.MaxValue && ResolveCatalog(catalog, (int)targetId) is { } known)
        {
            return known;
        }
        return ResolveRuntime(catalog, targetId);
    }

    private TranslocationTarget? ResolveCatalog(CatalogDbContext catalog, int targetId)
    {
        if (catalog.FindStation(targetId) is { } station)
        {
            return new(targetId, SystemObjects.FindSystem(catalog, station.SolarSystemId), station.Id, SpacePosition.Zero, 0);
        }
        if (catalog.FindCelestial(targetId) is { } body)
        {
            var type = catalog.FittingType(body.TypeId) ?? throw new InvalidDataException("Unknown destination type.");
            return new(
                targetId,
                SystemObjects.FindSystem(catalog, body.SolarSystemId),
                null,
                new(body.X, body.Y, body.Z),
                body.Radius ?? type.Radius ?? 0
            );
        }
        if (catalog.FindSystem(targetId) is not null)
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
        return null;
    }

    public PilotTranslocation Move(long pilotId, long expectedShipId, long targetId) => Move(pilotId, expectedShipId, Resolve(targetId));

    public PilotTranslocation Move(long pilotId, long expectedShipId, TranslocationTarget target)
    {
        // Take the resident owner before the shared movement gate. Bot execution
        // already owns the world when it requests these same views.
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using (database.EnterMovement())
        {
            // Loading a system can initialize its asteroid rows. Do that before
            // acquiring the move transaction's SQLite writer on this same world.
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
                double radius = catalog.RequireItem(ship.TypeId).Radius ?? 0;
                var point = TranslocationRules.Arrival(target, radius, destinationObjects);
                if (ship.Position is null)
                {
                    ship.Position = world.Positions.Add(new FlightPosition { ShipId = ship.Id }).Entity;
                    PilotTaskTrackingStore.Record(
                        world,
                        catalog,
                        pilotId,
                        clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                        TaskEvents.Undocked
                    );
                }
                ship.Position.SystemId = target.System.Id;
                (ship.Position.X, ship.Position.Y, ship.Position.Z) = (point.X, point.Y, point.Z);
            }
            ShipHeatPersistence.CancelForTravel(world, ship.Id);
            world.SaveChanges();
            var current = PilotFleetStore.Read(catalog, ship);
            transaction.Commit();
            LiveWorld.Find(database.Path)?.Heat.ClearSelection(pilotId, ship.Id);
            return new(lookup.FindPilot(pilot.AccountId, pilotId)!, new(current, previous));
        }
    }
}
