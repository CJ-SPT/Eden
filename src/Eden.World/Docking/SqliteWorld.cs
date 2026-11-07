using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Docking;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Corporations;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Docking;

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
        if (
            position is not null
            && systemId is int currentSystem
            && pilot is not null
            && world.Set<SavedCrimewatch>().Find(pilot.Id) is { } crimewatch
        )
        {
            crimewatch.SystemId = currentSystem;
        }
        if (pilot is not null && position is not null)
        {
            long now = _clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            if (saved is null)
            {
                PilotTaskTrackingStore.Record(world, catalog, pilot.Id, now, TaskEvents.Undocked);
            }
            else if (systemId is int destination)
            {
                int origin = saved.SystemId ?? catalog.FindStation(world.Ships.Find(shipId)!.StationId)!.SolarSystemId;
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
        var destination = catalog.FindStation(stationId);
        if (pilot.ActiveShipId != shipId || pilot.ActiveShip.Position is not { } position || destination is null)
        {
            throw new InvalidDataException("Dock your active ship at an available station.");
        }
        int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        CrimewatchPersistence.CheckTravel(
            world,
            catalog,
            pilot,
            Eden.Gameplay.Combat.CrimewatchTravel.Dock,
            _clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );
        double shipRadius = catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0;
        if (
            destination.SolarSystemId != system
            || new SpacePosition(position.X, position.Y, position.Z).DistanceTo(new(destination.X, destination.Y, destination.Z))
                > (catalog.RequireItem(destination.TypeId).Radius ?? 0) + shipRadius + DockingDefinitions.MaximumRange
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
            .Pilots.Where(p =>
                p.AccountId == accountId && !world.Set<SavedCharacterDeletion>().Any(d => d.PilotId == p.Id && d.DeletedAt != null)
            )
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToArray()
            .Select(id => FindPilot(accountId, id)!)
            .ToArray();
    }

    public DockedPilot? FindPilot(long accountId, long? pilotId = null)
    {
        using WorldDbContext world = new(_database.Path);
        using var transaction = world.Database.BeginTransaction();
        var result = FindPilot(world, accountId, pilotId);
        if (result is not null)
        {
            transaction.Commit();
        }
        return result;
    }

    internal DockedPilot? FindPilot(WorldDbContext world, long accountId, long? pilotId = null)
    {
        if (world.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Pilot snapshots require their owning world transaction.");
        }
        var pilot = world
            .Pilots.AsNoTracking()
            .Include(p => p.Account)
            .Include(p => p.ActiveShip)
                .ThenInclude(s => s.Position)
            .Where(p =>
                p.AccountId == accountId
                && (pilotId == null || p.Id == pilotId)
                && !world.Set<SavedCharacterDeletion>().Any(d => d.PilotId == p.Id && d.DeletedAt != null)
            )
            .OrderBy(p => p.Id)
            .FirstOrDefault();
        if (pilot is null)
        {
            return null;
        }
        var ship = pilot.ActiveShip;
        using CatalogDbContext catalog = new(_database.CatalogPath);
        var type = catalog.RequireItem(ship.TypeId);
        var station = catalog.RequireStation(ship.StationId);
        var system = catalog.RequireSystem(station.SolarSystemId);
        var stationType = catalog.RequireItem(station.TypeId);
        // Hulls with no weapon fittings can omit the zero-valued hardpoint attribute.
        int turretSlots = checked((int)catalog.FittingAttributes(type.Id).GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft));
        var fitted = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        var flight = new FlightProperties(
            (float)stationType.Radius!.Value,
            (float)type.Radius!.Value,
            fitted.GetValueOrDefault(DogmaAttributeId.Mass),
            (float)fitted.GetValueOrDefault(DogmaAttributeId.Agility),
            (float)fitted.GetValueOrDefault(DogmaAttributeId.MaxVelocity),
            fitted.GetValueOrDefault(DogmaAttributeId.BaseWarpSpeed) * fitted.GetValueOrDefault(DogmaAttributeId.WarpSpeedMultiplier)
        );
        var identity = new StationIdentity(catalog.StationName(station.Id), stationType.GroupId, stationType.CategoryId);
        var hull = new HullProperties(
            fitted.GetValueOrDefault(DogmaAttributeId.StructureHitPoints),
            fitted.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity),
            fitted.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime),
            fitted.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
            fitted.GetValueOrDefault(DogmaAttributeId.ArmorHitPoints),
            fitted.GetValueOrDefault(DogmaAttributeId.ShieldRechargeTime)
        );
        var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
        var corporationAccess = CorporationRoleStore.Effective(world, pilot.Id, corporation);
        DockedPilot result = new(
            new(
                pilot.Id,
                pilot.Name,
                pilot.CorporationId,
                new(pilot.Account.UserName, pilot.Account.CreatedAt, pilot.Account.CloneGrade),
                new(pilot.RaceId, pilot.BloodlineId, pilot.GenderId, pilot.AncestryId, pilot.SchoolId, pilot.CreatedAt),
                CorporationRoles: corporationAccess.Roles.At(
                    ship.Position is null ? ship.StationId : null,
                    corporation?.StationId ?? 0,
                    corporationAccess.BaseId
                ),
                CorporationAccess: corporationAccess,
                CorporationAccountKey: CorporationWalletStore.SelectedKey(world, pilot.Id, corporation)
            ),
            new(
                ship.Id,
                ship.TypeId,
                type.GroupId,
                type.CategoryId,
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
                new(system.Id, system.ConstellationId, system.RegionId),
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
                corporation?.Ticker ?? catalog.Read(CatalogData.Client.Npccorporations, (long)pilot.CorporationId)?.TickerName ?? "",
        };
        return result;
    }
}
