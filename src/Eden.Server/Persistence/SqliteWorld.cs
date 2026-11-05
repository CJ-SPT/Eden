using System.Collections.Concurrent;
using System.Globalization;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed record WorldDatabase(string Path)
{
    internal object MotionLock { get; } = new();
    internal ConcurrentDictionary<int, int> MotionStamps { get; } = new();
    internal ConcurrentDictionary<long, int> CollisionStamps { get; } = new();
    internal ConcurrentDictionary<int, long> PopulationStamps { get; } = new();
    public string CatalogPath =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!, "catalog.sqlite");
}

[Injectable(InjectionType.Singleton)]
public sealed partial class SqliteWorld : IDockedWorld, IStationDocking, ISystemMotionStore
{
    private readonly WorldDatabase _database;
    private readonly TimeProvider _clock;
    private readonly NpcProfileCatalog _npcProfiles;

    public SqliteWorld(WorldDatabase database, TimeProvider? clock = null, NpcProfileCatalog? npcProfiles = null)
    {
        _database = database;
        _npcProfiles = npcProfiles ?? new NpcProfileCatalog(cache: false);
        _clock = clock ?? TimeProvider.System;
    }

    public void SetPosition(long shipId, SpacePosition? position, int? systemId = null)
    {
        using WorldDbContext world = new(_database.Path);
        using var transaction = world.Database.BeginTransaction();
        var saved = world.Positions.Find(shipId);
        using CatalogDbContext catalog = new(_database.CatalogPath);
        var pilot = world.Pilots.SingleOrDefault(pilot => pilot.ActiveShipId == shipId);
        if (pilot is not null && position is not null)
        {
            long now = _clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            if (saved is null)
            {
                PilotTaskTrackingStore.Record(world, catalog, pilot.Id, now, TaskEvents.Undocked);
            }
            else if (systemId is int destination)
            {
                int origin = saved.SystemId ?? catalog.Set<Station>().Find(world.Ships.Find(shipId)!.StationId)!.SolarSystemId;
                if (origin != destination)
                {
                    PilotTaskTrackingStore.Record(world, catalog, pilot.Id, now, TaskEvents.Jumped);
                }
            }
        }
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
        return world
            .Set<Account>()
            .Where(a => a.Id == accountId)
            .Select(a => new AccountProfile(a.UserName, a.CreatedAt, a.CloneGrade))
            .Single();
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
        PilotTaskTrackingStore.Record(world, catalog, pilotId, _clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), TaskEvents.Docked);
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
        // Hulls with no weapon fittings can omit the zero-valued hardpoint attribute.
        int turretSlots = checked(
            (int)(type.Attributes.SingleOrDefault(a => a.AttributeId == (int)DogmaAttributeId.TurretSlotsLeft)?.Value ?? 0)
        );
        var fitted = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        var flight = new FlightProperties(
            (float)station.Type.Radius!.Value,
            (float)type.Radius!.Value,
            fitted.GetValueOrDefault(DogmaAttributeId.Mass),
            (float)fitted.GetValueOrDefault(DogmaAttributeId.Agility),
            (float)fitted.GetValueOrDefault(DogmaAttributeId.MaxVelocity),
            fitted.GetValueOrDefault(DogmaAttributeId.BaseWarpSpeed) * fitted.GetValueOrDefault(DogmaAttributeId.WarpSpeedMultiplier)
        );
        var designation = catalog.Set<StationDesignation>().Single(s => s.StationId == station.Id);
        var identity = new StationIdentity(
            designation.Format(system.Name, station.Owner.Name),
            station.Type.GroupId,
            station.Type.Group.CategoryId
        );
        var hull = new HullProperties(
            fitted.GetValueOrDefault(DogmaAttributeId.StructureHitPoints),
            fitted.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity),
            fitted.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime),
            fitted.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
            fitted.GetValueOrDefault(DogmaAttributeId.ArmorHitPoints),
            fitted.GetValueOrDefault(DogmaAttributeId.ShieldRechargeTime)
        );
        return new(
            new(
                pilot.Id,
                pilot.Name,
                pilot.CorporationId,
                new(pilot.Account.UserName, pilot.Account.CreatedAt, pilot.Account.CloneGrade),
                new(pilot.RaceId, pilot.BloodlineId, pilot.GenderId, pilot.AncestryId, pilot.SchoolId, pilot.CreatedAt)
            ),
            new(
                ship.Id,
                ship.TypeId,
                type.GroupId,
                type.Group.CategoryId,
                ship.Name,
                fitted.GetValueOrDefault(DogmaAttributeId.CargoCapacity),
                turretSlots,
                flight,
                hull
            ),
            new(
                station.Id,
                station.TypeId,
                station.OwnerId,
                new(system.Id, system.ConstellationId, system.Constellation.RegionId),
                station.OperationId,
                station.OrbitId,
                new(station.X, station.Y, station.Z),
                identity,
                StationUndocking.SelectPoint(ship.Id, type.GroupId, StationUndockCatalog.Read(catalog, station.TypeId))
            ),
            new(
                ship.Position is { } p ? new(p.X, p.Y, p.Z) : null,
                ship.Position?.SystemId is int id ? SystemObjects.FindSystem(catalog, id) : null
            )
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
