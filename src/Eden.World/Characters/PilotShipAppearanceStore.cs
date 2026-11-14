using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotShipAppearanceStore(WorldDatabase database, TimeProvider clock) : IPilotShipAppearance
{
    public ShipDirtState Read(long pilotId, long shipId) => Access(pilotId, shipId, clean: false);

    public ShipDirtState Clean(long pilotId, long shipId) => Access(pilotId, shipId, clean: true);

    private ShipDirtState Access(long pilotId, long shipId, bool clean)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        var pilot = world.Pilots.Find(pilotId);
        var ship = world.Filter<Ship>(s => s.Id == shipId && s.OwnerId == pilotId).SingleOrDefault();
        if (pilot is null || ship is null)
        {
            throw new InvalidDataException("This owned ship is unavailable.");
        }
        var item = ShipStoragePersistence.Location(ship);
        var parent = ship.StoredInId is { } parentId
            ? world.Filter<Ship>(s => s.Id == parentId && s.OwnerId == pilotId).SingleOrDefault()
            : null;
        if (
            !ShipStorageRules.CanRead(
                new(pilotId, pilot.ActiveShipId, pilot.ActiveShip.DockedLocationId, pilot.ActiveShip.Position is null),
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
        transaction.Commit();
        return new(shipId, state.Timestamp);
    }
}
