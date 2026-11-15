using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Inventory;

[Injectable]
internal sealed class CargoContainerAssemblyStore(WorldDatabase database) : ICargoContainerAssembly
{
    public IReadOnlyList<InventoryChange> Assemble(long pilotId, long itemId)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Container assembly requires a resident world.");
        using var world = live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId);
        if (pilot?.ActiveShip is not { Position: null } activeShip)
        {
            throw new InvalidDataException("Dock before assembling a cargo container.");
        }
        var items = world.Set<OwnedItem>();
        var item = items.Find(itemId);
        if (item is null || (item.OwnerId != pilotId && item.OwnerId != pilot.CorporationId))
        {
            throw new InvalidDataException("This container is unavailable.");
        }
        var before = PilotInventoryStore.Read(catalog, item);
        if (!CargoContainerAssemblyRules.IsContainer(before))
        {
            throw new InvalidDataException("Choose a cargo container to assemble.");
        }

        long station = activeShip.DockedLocationId;
        Ship? cargoShip = null;
        SavedCorporationOffice? office = null;
        if (item.OwnerId != pilotId)
        {
            office = CorporationStorageStore.LocalOffice(world, pilotId, item.LocationId);
            var corporation = CorporationRoleStore.Current(world, pilotId);
            var access = CorporationRoleStore.Effective(world, pilotId, corporation);
            if (
                !CorporationStorageRules.CanQuery(access, office.StationId, corporation.StationId, item.FlagId)
                || !CorporationStorageRules.CanTake(access, office.StationId, corporation.StationId, item.FlagId)
            )
            {
                throw new InvalidDataException("Viewing and withdrawal rights are required to assemble containers in this division.");
            }
        }
        else if (item.LocationId != station || item.FlagId != (int)InventoryFlag.Hangar)
        {
            cargoShip = item.FlagId == (int)InventoryFlag.Cargo ? world.Ships.Find(item.LocationId) : null;
            if (
                cargoShip is not { Position: null, StoredInId: null }
                || cargoShip.OwnerId != pilotId
                || cargoShip.DockedLocationId != station
            )
            {
                throw new InvalidDataException("Assemble the container in a local hangar or in your docked ship's cargo hold.");
            }
        }

        if (item.Singleton)
        {
            return [];
        }
        if (item.Online || items.Any(child => child.LocationId == item.Id) || world.Ships.Any(ship => ship.StoredInId == item.Id))
        {
            throw new InvalidDataException("Choose an empty packaged cargo container.");
        }
        if (cargoShip is not null)
        {
            double volume = item.VolumeOverride ?? PilotInventoryStore.Volume(catalog, item.TypeId, singleton: true);
            double used = InventoryPersistence.UsedVolume(world, catalog, pilotId, cargoShip.Id, item.FlagId);
            double capacity = ShipHoldRules.Capacity(item.FlagId, PilotFittingStore.ShipAttributes(world, catalog, pilot, cargoShip));
            CargoContainerAssemblyRules.CheckCapacity(before, volume, used, capacity);
        }

        IReadOnlyList<InventoryChange> changes;
        if (item.Quantity == 1)
        {
            item.Singleton = true;
            changes = [new(PilotInventoryStore.Read(catalog, item), before)];
        }
        else
        {
            item.Quantity--;
            var assembled = world
                .Add(
                    new OwnedItem
                    {
                        Id = world.NextItemId(),
                        OwnerId = item.OwnerId,
                        TypeId = item.TypeId,
                        LocationId = item.LocationId,
                        FlagId = item.FlagId,
                        Quantity = 1,
                        Singleton = true,
                        VolumeOverride = item.VolumeOverride,
                    }
                )
                .Entity;
            changes = [new(PilotInventoryStore.Read(catalog, item), before), new(PilotInventoryStore.Read(catalog, assembled), null)];
        }
        if (office is not null)
        {
            CorporationStorageStore.Notify(world, new(pilotId, office.Snapshot(), false, changes));
        }
        world.Commit();
        return changes;
    }
}
