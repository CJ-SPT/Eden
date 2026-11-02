using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotFleetStore(WorldDatabase database, SqliteWorld lookup) : IPilotFleet
{
    private static InventoryItem Read(CatalogDbContext catalog, Ship ship)
    {
        var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == ship.TypeId);
        return new(ship.Id, ship.TypeId, ship.OwnerId, ship.Position is null ? ship.StationId : ship.Position.SystemId ?? ship.StationId,
            ship.Position is null ? (int)InventoryFlag.Hangar : (int)InventoryFlag.AutoFit, 1, type.GroupId,
            type.Group.CategoryId, PilotInventoryStore.Volume(catalog, ship.TypeId), true);
    }

    public IReadOnlyList<InventoryItem> Ships(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return world.Ships.AsNoTracking().Include(s => s.Position).Where(s => s.OwnerId == pilotId && s.StationId == stationId && s.Position == null)
            .OrderBy(s => s.Id).AsEnumerable().Select(s => Read(catalog, s)).ToArray();
    }

    public InventoryItem? Find(long pilotId, long shipId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var ship = world.Ships.AsNoTracking().Include(s => s.Position).SingleOrDefault(s => s.Id == shipId && s.OwnerId == pilotId);
        return ship is null ? null : Read(catalog, ship);
    }

    public string Name(long pilotId, long shipId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Ships.Where(s => s.OwnerId == pilotId && s.Id == shipId).Select(s => s.Name).Single();
    }

    public ShipAssembly Assemble(long pilotId, IReadOnlyList<long> itemIds, string? name)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        if (pilot.ActiveShip.Position is not null || itemIds.Count == 0 || itemIds.Distinct().Count() != itemIds.Count ||
            name?.Length > FleetDefinitions.MaximumShipNameLength || name?.Any(char.IsControl) == true)
        {
            throw new InvalidDataException("Assemble distinct packaged ships in your current station hangar with a valid name.");
        }
        List<long> ships = [];
        List<InventoryChange> changes = [];
        long nextId = PilotInventoryStore.NextId(world);
        foreach (long id in itemIds)
        {
            // Retrying an assembled ID must not consume a second unit from its old stack.
            if (world.Ships.Include(s => s.Position).SingleOrDefault(s => s.Id == id) is { } assembled)
            {
                if (assembled.OwnerId != pilotId || assembled.StationId != pilot.ActiveShip.StationId || assembled.Position is not null)
                {
                    throw new InvalidDataException("The ship is unavailable in your current hangar.");
                }
                ships.Add(id);
                continue;
            }
            var item = world.Set<OwnedItem>().Find(id);
            if (item is null || item.OwnerId != pilotId || item.LocationId != pilot.ActiveShip.StationId ||
                item.FlagId != (int)InventoryFlag.Hangar || item.Singleton)
            {
                throw new InvalidDataException("The packaged ship is unavailable in your current hangar.");
            }
            var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == item.TypeId);
            if (type.Group.CategoryId != (int)InventoryCategory.Ship || type.Id != (int)StarterShipType.Venture)
            {
                throw new InvalidDataException("This hull is not supported for assembly yet.");
            }
            var before = PilotInventoryStore.Read(catalog, item);
            long shipId = item.Quantity == 1 ? id : nextId++;
            if (item.Quantity == 1) { world.Remove(item); }
            else
            {
                item.Quantity--;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
            }
            var ship = world.Ships.Add(new()
            {
                Id = shipId,
                OwnerId = pilotId,
                TypeId = item.TypeId,
                StationId = pilot.ActiveShip.StationId,
                Name = string.IsNullOrWhiteSpace(name) ? type.Name : name.Trim(),
            }).Entity;
            changes.Add(new(Read(catalog, ship), shipId == id ? before : null));
            ships.Add(shipId);
        }
        world.SaveChanges();
        transaction.Commit();
        return new(ships, changes);
    }

    public DockedPilot Board(long pilotId, long shipId, long previousShipId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var ship = world.Ships.Include(s => s.Position).SingleOrDefault(s => s.Id == shipId && s.OwnerId == pilotId);
        if (pilot.ActiveShip.Position is not null || ship is null || ship.Position is not null || ship.StationId != pilot.ActiveShip.StationId ||
            (pilot.ActiveShipId != previousShipId && pilot.ActiveShipId != shipId))
        {
            throw new InvalidDataException("Board an assembled ship you own at your current docked station.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, ship.TypeId);
        pilot.ActiveShipId = shipId;
        world.SaveChanges();
        transaction.Commit();
        return lookup.FindPilot(pilot.AccountId, pilotId)!;
    }
}
