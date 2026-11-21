using Eden.Data.World.Characters;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Inventory;

internal sealed partial class PilotShipSharingStore
{
    private const int MaintenanceFlag = (int)InventoryFlag.ShipMaintenanceBay;

    // Build 3662168 typelist.AllowedInSMACargoholds.
    private const int MaintenanceCargoTypes = 11;

    public SharedShipBay? Bay(long pilotId, long shipId)
    {
        var live = LiveWorld.Find(database.Path);
        if (live is null)
        {
            return null;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(
            world =>
            {
                var pilot = world.Pilots.Find(pilotId);
                if (pilot is null)
                {
                    return null;
                }
                var host = pilot.ActiveShipId == shipId ? pilot : Adjacent(world, catalog, pilot, shipId);
                if (
                    host is null
                    || host.Id != pilotId && !Permitted(world, pilot, host, maintenance: true)
                    || Capacity(world, catalog, host, MaintenanceFlag) is not (> 0 and var capacity)
                )
                {
                    return null;
                }
                var hold = ShipHoldRules.Definition(MaintenanceFlag)!;
                var vessels = Vessels(world, shipId).Select(ship => PilotFleetStore.Read(catalog, ship)).ToArray();
                return new SharedShipBay(
                    PilotFleetStore.Read(catalog, host.ActiveShip),
                    vessels,
                    capacity,
                    vessels.Sum(item => hold.RequiredCapacity(item, 1))
                );
            },
            new(pilots: [pilotId], ships: [shipId])
        );
    }

    public ShipBoarding Store(long pilotId, long shipId, long hostShipId)
    {
        using var world = BeginBayOperation(pilotId, shipId, hostShipId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var (pilot, host) = RequireBay(world, catalog, pilotId, shipId, hostShipId, now);
        var current = pilot.ActiveShip;
        if (catalog.RequireItem(current.TypeId).GroupId == FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidDataException("A capsule cannot be stored in a ship maintenance bay.");
        }
        var position = current.Position!;
        int system = CrimewatchPersistence.System(catalog, current);
        var type = catalog.RequireItem(FleetDefinitions.CapsuleType);
        var capsule = world
            .Add(
                new Ship
                {
                    Id = world.NextItemId(),
                    OwnerId = pilotId,
                    TypeId = type.Id,
                    StationId = current.StationId,
                    Name = type.Name,
                    Position = new FlightPosition
                    {
                        SystemId = system,
                        X = position.X,
                        Y = position.Y,
                        Z = position.Z,
                    },
                }
            )
            .Entity;
        var stored = StoreHull(world, catalog, pilot, host, now);
        var result = CompleteSwap(world, pilot, capsule, [stored, new(PilotFleetStore.Read(catalog, capsule), null)]);
        world.Commit();
        events.Publish(host.Id, [stored]);
        return result;
    }

    public ShipBoarding Board(long pilotId, long shipId, long hostShipId, long storedShipId)
    {
        using var world = BeginBayOperation(pilotId, shipId, hostShipId, storedShipId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var (pilot, host) = RequireBay(world, catalog, pilotId, shipId, hostShipId, now);
        var current = pilot.ActiveShip;
        var position = current.Position!;
        int system = CrimewatchPersistence.System(catalog, current);
        if (
            world.Ships.Find(storedShipId) is not { Position: null, StorageFlagId: MaintenanceFlag } target
            || target.StoredInId != hostShipId
            || target.OwnerId != pilotId
        )
        {
            throw new InvalidDataException("Choose an assembled ship you own in this maintenance bay.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, target.TypeId);
        var before = PilotFleetStore.Read(catalog, target);
        bool fromCapsule = catalog.RequireItem(current.TypeId).GroupId == FleetDefinitions.CapsuleGroup;
        var departing = fromCapsule
            ? new InventoryChange(PilotFleetStore.Read(catalog, current) with { Quantity = 0 }, PilotFleetStore.Read(catalog, current))
            : StoreHull(world, catalog, pilot, host, now, storedShipId);
        target.StoredInId = null;
        target.StorageFlagId = (int)InventoryFlag.Hangar;
        SqliteWorld.SetPosition(world, catalog, target.Id, new SpacePosition(position.X, position.Y, position.Z), system, now);
        var boarded = new InventoryChange(PilotFleetStore.Read(catalog, target), before);
        var result = CompleteSwap(world, pilot, target, [departing, boarded]);
        if (fromCapsule)
        {
            ShipStoragePersistence.Destroy(world, current);
        }
        world.Commit();
        events.Publish(host.Id, fromCapsule ? [boarded] : [departing, boarded]);
        return result;
    }

    private EconomyTransaction BeginBayOperation(long pilot, params long[] ships) =>
        (LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Ship maintenance requires the resident world.")).Economy.Begin(
            new TransactionParticipants(pilots: [pilot], ships: ships)
        );

    private (Pilot Pilot, Pilot Host) RequireBay(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        long hostShipId,
        long now
    )
    {
        var pilot = world.Pilots.Find(pilotId);
        if (
            pilot?.ActiveShipId != shipId
            || pilot.ActiveShip.OwnerId != pilotId
            || Adjacent(world, catalog, pilot, hostShipId, ShipSharingRules.FittingRange) is not { } host
            || !Permitted(world, pilot, host, maintenance: true)
            || Capacity(world, catalog, host, MaintenanceFlag) <= 0
        )
        {
            throw new InvalidDataException("Approach an available shared ship maintenance bay.");
        }
        Eden.World.Flight.PilotCapitalTravelStore.RequireMobile(world, pilotId, shipId, now);
        CrimewatchPersistence.CheckTravel(world, catalog, pilot, CrimewatchTravel.ChangeShip, now);
        var fit = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        if (PilotHealthStore.Read(world, shipId, ShipHealth.DefenseFrom(fit), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot use the maintenance bay.");
        }
        return (pilot, host);
    }

    private static IEnumerable<Ship> Vessels(WorldAction world, long shipId) =>
        world.Filter<Ship>(s => s.StoredInId == shipId && s.StorageFlagId == MaintenanceFlag).AsEnumerable().OrderBy(s => s.Id);

    private static InventoryChange StoreHull(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        Pilot host,
        long now,
        long replacing = 0
    )
    {
        var ship = pilot.ActiveShip;
        var before = PilotFleetStore.Read(catalog, ship);
        var hold = ShipHoldRules.Definition(MaintenanceFlag)!;
        if (
            !hold.Allows(before)
            || catalog.RequireItem(host.ActiveShip.TypeId).GroupId == (int)HullClass.CapitalIndustrial
                && catalog.FittingAttributes(ship.TypeId).GetValueOrDefault(DogmaAttributeId.CapitalIndustrialMaintenanceAllowed) <= 0
        )
        {
            throw new InvalidDataException("The ship is ineligible for this maintenance bay.");
        }
        var cargo = CatalogTypeLists.Read(catalog, MaintenanceCargoTypes);
        ShipStorageRules.CheckContents(
            PilotInventoryStore.List(world, catalog, pilot.Id, ship.Id),
            world.Ships.Any(s => s.StoredInId == ship.Id),
            cargo.Contains
        );
        double available =
            Capacity(world, catalog, host, MaintenanceFlag)
            - Vessels(world, host.ActiveShipId)
                .Where(stored => stored.Id != replacing)
                .Sum(stored => hold.RequiredCapacity(PilotFleetStore.Read(catalog, stored), 1));
        if (hold.RequiredCapacity(before, 1) > available)
        {
            throw new InvalidDataException("The ship maintenance bay has insufficient space.");
        }
        PilotDroneStore.Release(world, pilot.Id, ship.Id);
        SqliteWorld.SetPosition(world, catalog, ship.Id, null, null, now);
        ship.StoredInId = host.ActiveShipId;
        ship.StorageFlagId = MaintenanceFlag;
        ship.StructureId = null;
        return new(PilotFleetStore.Read(catalog, ship), before);
    }

    private ShipBoarding CompleteSwap(WorldAction world, Pilot pilot, Ship current, IReadOnlyList<InventoryChange> changes)
    {
        pilot.ActiveShip = current;
        pilot.ActiveShipId = current.Id;
        world.SaveChanges();
        return new(lookup.FindPilot(world, pilot.AccountId, pilot.Id)!, changes);
    }
}
