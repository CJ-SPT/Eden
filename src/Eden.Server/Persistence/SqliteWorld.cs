using System.Globalization;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed record WorldDatabase(string Path)
{
    public string CatalogPath =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!, "catalog.sqlite");
}

[Injectable(InjectionType.Singleton)]
public sealed class SqliteWorld : IDockedWorld, IStationDocking
{
    private readonly WorldDatabase _database;

    public SqliteWorld(WorldDatabase database)
    {
        _database = database;
        if (!File.Exists(database.CatalogPath))
        {
            throw new FileNotFoundException(
                "Build the local game catalog with tools/catalog/build.py before starting Eden.",
                database.CatalogPath
            );
        }
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        WorldMigration.Open(world, catalog);
    }

    public SqliteWorld(string path)
        : this(new WorldDatabase(path)) { }

    public void SetPosition(long shipId, SpacePosition? position, int? systemId = null)
    {
        using WorldDbContext world = new(_database.Path);
        using var transaction = world.Database.BeginTransaction();
        var saved = world.Positions.Find(shipId);
        if (position is null)
        {
            if (saved is not null)
            {
                world.Positions.Remove(saved);
            }
        }
        else
        {
            saved ??= world.Positions.Add(new FlightPosition { ShipId = shipId }).Entity;
            saved.SystemId = systemId ?? saved.SystemId;
            (saved.X, saved.Y, saved.Z) = (position.X, position.Y, position.Z);
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public AccountProfile ReadAccount(long accountId)
    {
        using WorldDbContext world = new(_database.Path);
        return world.Set<Account>().Where(a => a.Id == accountId).Select(a => new AccountProfile(a.UserName, a.CreatedAt)).Single();
    }

    public DockedPilot Dock(long pilotId, long shipId, int stationId)
    {
        using WorldDbContext world = new(_database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(_database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var destination = catalog.Set<Station>().Include(s => s.Type).SingleOrDefault(s => s.Id == stationId);
        if (pilot.ActiveShipId != shipId || pilot.ActiveShip.Position is not { } position || destination is null)
        {
            throw new InvalidDataException("Dock your active ship at an available station.");
        }
        int system = position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        double shipRadius = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId).Radius ?? 0;
        if (
            destination.SolarSystemId != system
            || new SpacePosition(position.X, position.Y, position.Z).DistanceTo(new(destination.X, destination.Y, destination.Z))
                > (destination.Type.Radius ?? 0) + shipRadius + DockingDefinitions.MaximumRange
        )
        {
            throw new InvalidDataException("The station is outside docking range.");
        }
        pilot.ActiveShip.StationId = stationId;
        world.Positions.Remove(position);
        world.SaveChanges();
        transaction.Commit();
        return FindPilot(pilot.AccountId, pilotId)!;
    }

    public IReadOnlyList<DockedPilot> ListPilots(long accountId)
    {
        using WorldDbContext world = new(_database.Path);
        return world
            .Pilots.Where(p => p.AccountId == accountId)
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToArray()
            .Select(id => FindPilot(accountId, id)!)
            .ToArray();
    }

    public DockedPilot? FindPilot(long accountId, long? pilotId = null)
    {
        using WorldDbContext world = new(_database.Path);
        var pilot = world
            .Pilots.AsNoTracking()
            .Include(p => p.Account)
            .Include(p => p.ActiveShip)
                .ThenInclude(s => s.Position)
            .Where(p => p.AccountId == accountId && (pilotId == null || p.Id == pilotId))
            .OrderBy(p => p.Id)
            .FirstOrDefault();
        if (pilot is null)
        {
            return null;
        }
        var ship = pilot.ActiveShip;
        using CatalogDbContext catalog = new(_database.CatalogPath);
        var type = catalog.Set<ItemType>().Include(t => t.Group).Include(t => t.Attributes).Single(t => t.Id == ship.TypeId);
        var station = catalog
            .Set<Station>()
            .Include(s => s.Type)
                .ThenInclude(t => t.Group)
            .Include(s => s.Owner)
            .Include(s => s.SolarSystem)
                .ThenInclude(s => s.Constellation)
            .Single(s => s.Id == ship.StationId);
        var system = station.SolarSystem;
        double Attribute(int id) => type.Attributes.Single(a => a.AttributeId == id).Value;
        var flight = new FlightProperties(
            (float)station.Type.Radius!.Value,
            (float)type.Radius!.Value,
            type.Mass!.Value,
            (float)Attribute(70),
            (float)PilotPropulsionStore.NavigationSpeed(world, catalog, pilot.Id, Attribute(37)),
            Attribute(1281) * Attribute(600)
        );
        var designation = catalog.Set<StationDesignation>().Single(s => s.StationId == station.Id);
        var identity = new StationIdentity(
            designation.Format(system.Name, station.Owner.Name),
            station.Type.GroupId,
            station.Type.Group.CategoryId
        );
        var hull = new HullProperties(Attribute(9), Attribute(482), Attribute(55), Attribute(263), Attribute(265), Attribute(479));
        return new(
            pilot.Id,
            pilot.Name,
            pilot.CorporationId,
            ship.Id,
            ship.TypeId,
            type.GroupId,
            type.Group.CategoryId,
            station.Id,
            station.TypeId,
            station.OwnerId,
            system.Id,
            system.ConstellationId,
            system.Constellation.RegionId,
            station.OperationId,
            station.OrbitId,
            station.X,
            station.Y,
            station.Z,
            ship.Name,
            type.Capacity!.Value,
            checked((int)Attribute(102)),
            flight,
            identity,
            hull,
            new(pilot.Account.UserName, pilot.Account.CreatedAt),
            new(pilot.RaceId, pilot.BloodlineId, pilot.GenderId, pilot.AncestryId, pilot.SchoolId, pilot.CreatedAt),
            ship.Position is { } p ? new(p.X, p.Y, p.Z) : null,
            ship.Position?.SystemId is int id ? SystemObjects.FindSystem(catalog, id) : null
        )
        {
            CorporationTicker =
                catalog
                    .Set<SourceRecord>()
                    .Find("client/npccorporations", pilot.CorporationId.ToString(CultureInfo.InvariantCulture))
                    ?.Read<ClientNpccorporations>()
                    .TickerName
                ?? "",
        };
    }
}
