using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Planetary;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Planetary;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Planetary;

internal static class PlanetaryOrbitStore
{
    internal const int InterbusCorporation = 1000148;
    private const double OfficeCapacity = 35000;
    private const double OrbitAltitude = 1_000_000;

    internal static void EnsureOffice(WorldAction world, PlanetaryPlanet planet)
    {
        if (world.Set<SavedPlanetaryOffice>().Find(planet.Id) is not null)
        {
            return;
        }
        world.Add(
            new SavedPlanetaryOffice
            {
                PlanetId = planet.Id,
                SystemId = planet.SystemId,
                X = planet.Position.X + planet.Radius + OrbitAltitude,
                Y = planet.Position.Y,
                Z = planet.Position.Z,
            }
        );
    }

    internal static long NextExpiry(WorldAction world, int systemId, long now) =>
        world.Filter<SavedPlanetaryLaunch>(l => l.SystemId == systemId && l.ExpiresAt > now).Min(l => (long?)l.ExpiresAt) ?? long.MaxValue;

    internal static IReadOnlyList<WreckContact> Contacts(WorldAction world, CatalogDbContext catalog, int systemId, long now)
    {
        var offices = world.Filter<SavedPlanetaryOffice>(o => o.SystemId == systemId).ToArray();
        var launches = world.Filter<SavedPlanetaryLaunch>(l => l.SystemId == systemId && l.ExpiresAt > now).ToArray();
        if (offices.Length == 0 && launches.Length == 0)
        {
            return [];
        }
        var officeType = catalog.RequireItem(PlanetaryRules.CustomsOfficeType);
        var launchType = catalog.RequireItem(PlanetaryRules.LaunchContainerType);
        var ids = launches.Select(l => (long)l.Id).ToHashSet();
        var occupied = world.Filter<OwnedItem>(i => ids.Contains(i.LocationId)).Select(i => i.LocationId).Distinct().ToHashSet();
        return offices
            .Select(o => new WreckContact(
                PlanetaryRules.OfficeId(o.PlanetId),
                o.PlanetId,
                InterbusCorporation,
                InterbusCorporation,
                officeType.Id,
                "Customs Office",
                systemId,
                new(o.X, o.Y, o.Z),
                officeType.Mass ?? 0,
                (float)(officeType.Radius ?? 0),
                false,
                officeType.GroupId,
                officeType.CategoryId,
                o.PlanetId
            ))
            .Concat(
                launches.Select(l => new WreckContact(
                    l.Id,
                    l.PlanetId,
                    l.PilotId,
                    0,
                    launchType.Id,
                    "Planetary Launch Container",
                    systemId,
                    new(l.X, l.Y, l.Z),
                    launchType.Mass ?? 0,
                    (float)(launchType.Radius ?? 0),
                    !occupied.Contains(l.Id),
                    launchType.GroupId,
                    launchType.CategoryId
                ))
            )
            .ToArray();
    }

    private static (Pilot Pilot, int SystemId, int TypeId, SpacePosition Position) Access(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long containerId,
        long now
    )
    {
        var pilot =
            world.Filter<Pilot>(p => p.Id == pilotId).SingleOrDefault()
            ?? throw new InvalidDataException("Choose an existing pilot before accessing orbital cargo.");
        int systemId;
        int typeId;
        SpacePosition target;
        if (
            PlanetaryRules.IsOffice(containerId)
            && world.Set<SavedPlanetaryOffice>().Find(PlanetaryRules.OfficePlanet(containerId)) is { } office
        )
        {
            systemId = office.SystemId;
            typeId = PlanetaryRules.CustomsOfficeType;
            target = new(office.X, office.Y, office.Z);
        }
        else if (
            PlanetaryRules.IsLaunch(containerId)
            && world.Set<SavedPlanetaryLaunch>().Find((int)containerId) is { } launch
            && launch.PilotId == pilotId
            && launch.ExpiresAt > now
        )
        {
            systemId = launch.SystemId;
            typeId = PlanetaryRules.LaunchContainerType;
            target = new(launch.X, launch.Y, launch.Z);
        }
        else
        {
            throw new InvalidDataException("This orbital container is unavailable or belongs to another pilot.");
        }
        if (
            pilot.ActiveShip.Position is not { } position
            || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != systemId
            || world.Set<SpatialNavigationState>().Find(pilot.ActiveShipId) is { CloakModuleId: not 0 }
        )
        {
            throw new InvalidDataException("Approach the orbital container in an uncloaked ship in its solar system.");
        }
        double distance =
            new SpacePosition(position.X, position.Y, position.Z).DistanceTo(target)
            - (catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0)
            - (catalog.RequireItem(typeId).Radius ?? 0);
        if (distance > WreckDefinitions.TransferRange)
        {
            throw new InvalidDataException("Approach within 2,500 m of the orbital container.");
        }
        return (pilot, systemId, typeId, target);
    }

    internal static PlanetaryOrbitInventory Read(WorldAction world, CatalogDbContext catalog, long pilotId, long containerId, long now)
    {
        var (_, systemId, typeId, _) = Access(world, catalog, pilotId, containerId, now);
        var type = catalog.RequireItem(typeId);
        return new(
            new(
                containerId,
                typeId,
                pilotId,
                systemId,
                (int)InventoryFlag.AutoFit,
                1,
                type.GroupId,
                type.CategoryId,
                PilotInventoryStore.Volume(catalog, typeId),
                true
            ),
            PilotInventoryStore.List(world, catalog, pilotId, containerId)
        );
    }

    internal static IReadOnlyList<InventoryChange> Move(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        long containerId,
        bool toOrbit,
        InventoryMoveRequest request,
        long now
    ) => Move(new SqlWorldAction(world), catalog, pilotId, containerId, toOrbit, request, now);

    internal static IReadOnlyList<InventoryChange> Move(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long containerId,
        bool toOrbit,
        InventoryMoveRequest request,
        long now
    )
    {
        var (pilot, _, _, _) = Access(world, catalog, pilotId, containerId, now);
        if (!toOrbit)
        {
            if (request.SourceId != containerId)
            {
                throw new InvalidDataException("Select cargo in this orbital container.");
            }
            return SpaceLootTransfer.Take(
                world,
                catalog,
                pilot,
                pilot.ActiveShipId,
                containerId,
                request.ItemIds,
                request.Quantity,
                (InventoryFlag)request.FlagId
            );
        }
        if (
            !PlanetaryRules.IsOffice(containerId)
            || request.SourceId != pilot.ActiveShipId
            || request.FlagId != (int)InventoryFlag.Hangar
            || request.ItemIds.Count is < 1 or > 128
            || request.ItemIds.Distinct().Count() != request.ItemIds.Count
            || request.Quantity is <= 0
            || request.Quantity.HasValue && request.ItemIds.Count != 1
        )
        {
            throw new InvalidDataException("Transfer planetary commodities from your ship into the customs office.");
        }
        var definitions = PlanetaryCatalogReader.Read(catalog);
        List<InventoryChange> changes = [];
        foreach (long itemId in request.ItemIds)
        {
            var item = world.Set<OwnedItem>().Find(itemId);
            int quantity = request.Quantity ?? item?.Quantity ?? 0;
            if (
                item is null
                || item.OwnerId != pilotId
                || item.LocationId != pilot.ActiveShipId
                || !ShipHoldRules.IsHold(item.FlagId)
                || item.Singleton
                || quantity <= 0
                || quantity > item.Quantity
                || !definitions.Volumes.ContainsKey(item.TypeId)
            )
            {
                throw new InvalidDataException("The selected commodities are unavailable in your ship's holds.");
            }
            var previous = PilotInventoryStore.Read(catalog, item);
            item.Quantity -= quantity;
            changes.Add(new(PilotInventoryStore.Read(catalog, item), previous));
            if (item.Quantity == 0)
            {
                world.Remove(item);
            }
            changes.Add(
                InventoryPersistence.MergeOutput(world, catalog, pilotId, containerId, (int)InventoryFlag.Hangar, item.TypeId, quantity)
            );
        }
        CheckOfficeCapacity(world, catalog, pilotId, containerId);
        return changes;
    }

    internal static void CheckOfficeCapacity(WorldAction world, CatalogDbContext catalog, long pilotId, long containerId)
    {
        double used = world
            .Filter<OwnedItem>(i => i.OwnerId == pilotId && i.LocationId == containerId)
            .AsEnumerable()
            .Concat(world.Set<OwnedItem>().Local)
            .DistinctBy(i => i.Id)
            .Where(i => i.OwnerId == pilotId && i.LocationId == containerId && world.Entry(i).State != EntityState.Deleted)
            .Sum(i => PilotInventoryStore.Volume(catalog, i.TypeId) * i.Quantity);
        if (used > OfficeCapacity + InventoryDefinitions.ResourceTolerance)
        {
            throw new CargoCapacityException(OfficeCapacity, used);
        }
    }

    internal static void DeleteLaunch(WorldAction world, SavedPlanetaryLaunch launch)
    {
        world.RemoveRange(world.Filter<OwnedItem>(i => i.LocationId == launch.Id));
        world.Remove(launch);
    }
}
