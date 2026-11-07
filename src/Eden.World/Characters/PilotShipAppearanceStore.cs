using Eden.Data.World.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotShipAppearanceStore(WorldDatabase database, TimeProvider clock) : IPilotShipAppearance
{
    public ShipDirtState Read(long pilotId, long shipId) => Access(pilotId, shipId, clean: false);

    public ShipDirtState Clean(long pilotId, long shipId) => Access(pilotId, shipId, clean: true);

    private ShipDirtState Access(long pilotId, long shipId, bool clean)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilotId);
        var ship = world.Ships.Include(s => s.Position).SingleOrDefault(s => s.Id == shipId && s.OwnerId == pilotId);
        if (pilot is null || ship is null)
        {
            throw new InvalidDataException("This owned ship is unavailable.");
        }
        var item = ShipStoragePersistence.Location(ship);
        var parent = ship.StoredInId is { } parentId
            ? world.Ships.Include(s => s.Position).SingleOrDefault(s => s.Id == parentId && s.OwnerId == pilotId)
            : null;
        if (
            !ShipStorageRules.CanRead(
                new(pilotId, pilot.ActiveShipId, pilot.ActiveShip.StationId, pilot.ActiveShip.Position is null),
                item,
                parent is null ? null : ShipStoragePersistence.Location(parent)
            )
        )
        {
            throw new InvalidDataException("Use your active ship or an accessible ship in the current station or ship bay.");
        }
        var state = world.Set<SavedShipDirt>().Find(shipId);
        if (state is null)
        {
            // Existing worlds have no reliable assembly/last-cleaning history.
            // Establish the baseline once, then keep aging across every later read.
            state = world
                .Add(
                    new SavedShipDirt
                    {
                        ShipId = shipId,
                        Timestamp = ShipDirtRules.CleanTimestamp(null, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
                    }
                )
                .Entity;
        }
        else if (clean)
        {
            state.Timestamp = ShipDirtRules.CleanTimestamp(state.Timestamp, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        }
        world.SaveChanges();
        transaction.Commit();
        return new(shipId, state.Timestamp);
    }
}
