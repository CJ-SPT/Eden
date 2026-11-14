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
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Docking;

public sealed partial class SqliteWorld
{
    private void SetResidentPosition(LiveWorld live, long shipId, SpacePosition? position, int? systemId)
    {
        using CatalogDbContext catalog = new(_database.CatalogPath);
        using var action = live.Economy.Begin(new(systems: systemId is int system ? [system] : [], ships: [shipId]));
        long? owner = action.Ships.Find(shipId)?.OwnerId;
        bool transfer = SetPosition(action, catalog, shipId, position, systemId, _clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        action.Commit();
        if (transfer && owner is long pilot)
        {
            live.Heat.ClearSelection(pilot, shipId);
        }
    }

    private static long[] ResidentPilotIds(LiveWorld live, long accountId) =>
        live.Economy.Inspect(world =>
            world
                .Filter<Pilot>(p => p.AccountId == accountId)
                .Select(p => p.Id)
                .AsEnumerable()
                .Where(id => world.Set<SavedCharacterDeletion>().Find(id)?.DeletedAt is null)
                .Order()
                .ToArray()
        );

    internal static bool SetPosition(
        WorldAction world,
        CatalogDbContext catalog,
        long shipId,
        SpacePosition? position,
        int? systemId,
        long now
    )
    {
        var saved = world.Positions.Find(shipId);
        if (position is not null && systemId is int sameSystem && saved?.SystemId == sameSystem)
        {
            // Ordinary movement changes coordinates only. Membership, crimewatch,
            // task tracking and CONCORD are handled when the ship enters or transfers.
            (saved.X, saved.Y, saved.Z) = (position.X, position.Y, position.Z);
            world.SaveChanges();
            return false;
        }
        var hull = world.Ships.Find(shipId);
        var owner = hull is null ? null : world.Pilots.Find(hull.OwnerId);
        var pilot = owner?.ActiveShipId == shipId ? owner : null;
        bool transfer =
            position is null
            || systemId is int destinationSystem
                && saved is not null
                && (saved.SystemId ?? catalog.RequireStation(hull!.StationId).SolarSystemId) != destinationSystem;
        bool entered = position is not null && (saved is null || transfer);
        if (transfer)
        {
            ShipHeatPersistence.CancelForTravel(world, shipId);
            if (world is EconomyTransaction)
            {
                Eden.World.Fleets.CommandBurstStore.Clear(world, shipId);
            }
        }
        if (saved is not null && systemId is int nextSystem && saved.SystemId != nextSystem)
        {
            PilotRemoteAssistanceStore.ClearForTravel(world, shipId, nextSystem);
        }
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
            if (saved is null)
            {
                PilotTaskTrackingStore.Record(world, catalog, pilot.Id, now, TaskEvents.Undocked);
            }
            else if (systemId is int destination)
            {
                int origin = saved.SystemId ?? catalog.RequireStation(hull!.StationId).SolarSystemId;
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
            if (saved is null)
            {
                hull!.StructureId = null;
            }
            saved ??= world.Positions.Add(new FlightPosition { ShipId = shipId }).Entity;
            saved.SystemId = systemId ?? saved.SystemId;
            (saved.X, saved.Y, saved.Z) = (position.X, position.Y, position.Z);
        }
        world.SaveChanges();
        if (entered && pilot is not null)
        {
            ConcordPersistence.Schedule(world, catalog, pilot, now);
            world.SaveChanges();
        }
        return transfer;
    }

    internal DockedPilot Dock(WorldAction world, CatalogDbContext catalog, long pilotId, long shipId, int stationId)
    {
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
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
        pilot.ActiveShip.StructureId = null;
        world.Positions.Remove(position);
        ShipHeatPersistence.CancelForTravel(world, shipId);
        world.SaveChanges();
        return FindPilot(world, pilot.AccountId, pilotId)!;
    }

    internal DockedPilot? FindPilot(WorldAction world, long accountId, long? pilotId = null)
    {
        var candidates = pilotId is long selected
            ? world.Filter<Pilot>(p => p.Id == selected && p.AccountId == accountId)
            : world.Filter<Pilot>(p => p.AccountId == accountId);
        var pilot = candidates
            .AsEnumerable()
            .OrderBy(p => p.Id)
            .FirstOrDefault(p => world.Set<SavedCharacterDeletion>().Find(p.Id)?.DeletedAt is null);
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
                    ship.Position is null
                        ? ship.StructureId.HasValue
                            ? 0
                            : ship.StationId
                        : null,
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
        return ship.StructureId is long structureId ? StructurePilot(world, catalog, result, structureId) : result;
    }
}
