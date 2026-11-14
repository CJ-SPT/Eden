using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Drones;

internal sealed partial class PilotDroneStore
{
    public IReadOnlyList<InventoryChange> Scoop(long pilotId, IReadOnlyList<long> drones, InventoryFlag hold, long now)
    {
        if (hold is not (InventoryFlag.Cargo or InventoryFlag.DroneBay) || drones.Count == 0 || drones.Distinct().Count() != drones.Count)
        {
            throw new InvalidDataException("Choose distinct lost drones and a valid ship hold.");
        }
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = InSpace(world, pilotId, requireDroneHull: false);
        var ship = pilot.ActiveShip;
        var attributes = FittingCalculation.Read(world, catalog, pilot).Graph.Attributes(ship.Id);
        if (PilotHealthStore.Read(world, ship.Id, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot recover drones.");
        }
        var position = Position(ship.Position!);
        int system = CurrentSystem(catalog, pilot);
        double shipRadius = catalog.FittingType(ship.TypeId)?.Radius ?? 0;
        double available =
            ShipHoldRules.Capacity((int)hold, attributes) - InventoryPersistence.UsedVolume(world, catalog, pilotId, ship.Id, (int)hold);
        List<InventoryChange> changes = [];
        foreach (long id in drones)
        {
            var drone = world.Set<WorldDrone>().Find(id);
            var item = world.Set<OwnedItem>().Find(id);
            if (
                drone is null
                || drone.ControllerShipId != 0
                || drone.SystemId != system
                || item is null
                || item.OwnerId != drone.PilotId
                || item.LocationId != system
                || !item.Singleton
                || item.Quantity != 1
            )
            {
                throw new InvalidDataException("Only uncontrolled drones in your current system can be scooped.");
            }
            var profile = DroneCatalog.Profile(catalog, item.TypeId);
            if (position.DistanceTo(new(drone.X, drone.Y, drone.Z)) - shipRadius - profile.Radius > DroneRules.RecallRange)
            {
                throw new InvalidDataException("Approach within 2,500 m of the drone to scoop it.");
            }
            var previous = PilotInventoryStore.Read(catalog, item);
            available -= previous.Volume;
            if (available < -InventoryDefinitions.ResourceTolerance)
            {
                throw new InvalidDataException("There is not enough room in the selected ship hold.");
            }
            item.LocationId = ship.Id;
            item.OwnerId = pilotId;
            item.FlagId = (int)hold;
            drone.SystemId = null;
            drone.PilotId = pilotId;
            drone.CorporationId = pilot.CorporationId;
            Release(drone);
            changes.Add(new(PilotInventoryStore.Read(catalog, item), previous));
        }
        transaction.Commit();
        return changes;
    }
}
