using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Runtime.Economy;

namespace Eden.World.Inventory;

internal sealed partial class CargoContainerStore
{
    public IReadOnlyList<InventoryChange> Move(long pilotId, long destination, InventoryMoveRequest request, CargoContainerAccess access)
    {
        using var world = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (Eden.World.Structures.AssetSafetyStore.Ancestor(world, request.SourceId) is not null)
        {
            var recovered = Eden.World.Structures.AssetSafetyStore.Recover(
                world,
                catalog,
                pilotId,
                destination,
                request,
                clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
            );
            world.Commit();
            return recovered.Changes;
        }
        if (request.ItemIds.Any(id => world.Set<Eden.Data.World.Structures.SavedAssetSafety>().Find(id) is not null))
        {
            throw new InvalidDataException("Recover safety contents before moving the wrap.");
        }
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        var ship = pilot.ActiveShip;
        bool depositing = world.Set<OwnedItem>().Find(destination) is not null;
        var container = Require(world, catalog, pilotId, depositing ? destination : request.SourceId);
        RequireOpen(world, catalog, container, access);
        if (container.OwnerId != pilotId)
        {
            throw new InvalidDataException("Transfer container contents through personal inventory.");
        }
        if (
            !depositing
            && destination == ship.Id
            && (request.FlagId == (int)InventoryFlag.AutoFit || InventoryDefinitions.IsFittedSlot(request.FlagId))
        )
        {
            if (request.Quantity.HasValue)
            {
                throw new InvalidDataException("Fit individual modules without a transfer quantity.");
            }
            var fitted = PilotFittingStore.Fit(
                world,
                catalog,
                pilotId,
                request.ItemIds,
                request.SourceId,
                request.FlagId,
                clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                containerAccess: access
            );
            world.Commit();
            return fitted;
        }
        long looseLocation = depositing ? request.SourceId : destination;
        bool hangar = ship.Position is null && looseLocation == ship.DockedLocationId;
        var looseShip = PilotInventoryStore.InventoryShip(world, pilot, looseLocation);
        bool cargo = looseShip is not null;
        if (
            (!hangar && !cargo)
            || request.ItemIds.Count == 0
            || request.ItemIds.Distinct().Count() != request.ItemIds.Count
            || request.Quantity is <= 0
            || request.Quantity.HasValue && request.ItemIds.Count != 1
            || !depositing && ship.Position is not null && request.FlagId != (int)InventoryFlag.Cargo
            || !depositing && !(hangar && request.FlagId == (int)InventoryFlag.Hangar || cargo && ShipHoldRules.IsHold(request.FlagId))
            || depositing && request.FlagId is not ((int)InventoryFlag.None or (int)InventoryFlag.Unlocked or (int)InventoryFlag.Locked)
        )
        {
            throw new InvalidDataException("Move distinct items between this container and your current hangar or cargo hold.");
        }
        var items = request
            .ItemIds.Select(id => world.Set<OwnedItem>().Find(id) ?? throw new InvalidDataException("A selected item is unavailable."))
            .ToArray();
        if (
            items.Any(item =>
                item.Id == container.Id
                || item.OwnerId != pilotId
                || item.LocationId != request.SourceId
                || PilotIndustryStore.IsInstalled(world, item.Id)
                || item.Singleton && CargoContainerAssemblyRules.IsContainer(PilotInventoryStore.Read(catalog, item))
                || (
                    depositing
                        ? !PilotInventoryStore.AccessibleItems(world, pilot, item.LocationId, item.FlagId)
                            || ship.Position is not null && item.FlagId != (int)InventoryFlag.Cargo
                        : item.FlagId is not ((int)InventoryFlag.None or (int)InventoryFlag.Unlocked)
                )
                || request.Quantity > item.Quantity
            )
        )
        {
            throw new InvalidDataException("Choose available, unlocked cargo. Assembled containers cannot be nested.");
        }
        var hold = !depositing && cargo ? ShipHoldRules.Definition(request.FlagId) : null;
        if (hold is not null && items.Any(item => !hold.Allows(PilotInventoryStore.Read(catalog, item))))
        {
            throw new InvalidDataException("This hold does not permit the requested cargo.");
        }
        double added = items.Sum(item => PilotInventoryStore.Read(catalog, item).Volume * (request.Quantity ?? item.Quantity));
        if (depositing || cargo)
        {
            double capacity = depositing
                ? catalog.RequireItem(container.TypeId).Capacity ?? 0
                : ShipHoldRules.Capacity(request.FlagId, PilotFittingStore.ShipAttributes(world, catalog, pilot, looseShip!));
            if (!depositing && capacity <= 0 && request.FlagId != (int)InventoryFlag.Cargo)
            {
                throw new InvalidDataException("This hold is unavailable.");
            }
            double used = PilotInventoryStore
                .List(world, catalog, pilotId, destination)
                .Where(item => depositing || item.FlagId == request.FlagId)
                .Sum(item => item.Volume * item.Quantity);
            if (added > capacity - used + InventoryDefinitions.ResourceTolerance)
            {
                throw new CargoCapacityException(Math.Max(0, capacity - used), added);
            }
        }
        int flag = depositing ? AddedFlag(world, catalog, container) : request.FlagId;
        List<InventoryChange> changes = [];
        foreach (var item in items)
        {
            var before = PilotInventoryStore.Read(catalog, item);
            int quantity = request.Quantity ?? item.Quantity;
            if (quantity < item.Quantity)
            {
                item.Quantity -= quantity;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
                var split = world
                    .Add(
                        new OwnedItem
                        {
                            Id = world.NextItemId(),
                            OwnerId = pilotId,
                            TypeId = item.TypeId,
                            LocationId = destination,
                            FlagId = flag,
                            Quantity = quantity,
                            Singleton = item.Singleton,
                            CrystalDamage = item.CrystalDamage,
                            VolumeOverride = item.VolumeOverride,
                            BlueprintRuns = item.BlueprintRuns,
                            MaterialEfficiency = item.MaterialEfficiency,
                            TimeEfficiency = item.TimeEfficiency,
                        }
                    )
                    .Entity;
                changes.Add(new(PilotInventoryStore.Read(catalog, split), null));
            }
            else
            {
                item.LocationId = destination;
                item.FlagId = flag;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
            }
        }
        world.Commit();
        return changes;
    }

    public IReadOnlyList<InventoryChange> Lock(long pilotId, long itemId, IReadOnlyList<long> ids, bool locked, CargoContainerAccess access)
    {
        using var world = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var container = Require(world, catalog, pilotId, itemId, administer: true);
        CheckAuditContainer(catalog, container);
        var settings = world.Set<SavedCargoContainer>().Find(itemId);
        var required = locked ? CargoContainerConfiguration.PasswordToLock : CargoContainerConfiguration.PasswordToUnlock;
        if (settings?.Configuration.HasFlag(required) == true)
        {
            RequirePassword(settings, CargoContainerPasswordType.General, access);
        }
        var items = ids.Distinct().Select(id => world.Set<OwnedItem>().Find(id)).ToArray();
        if (items.Length == 0 || items.Any(item => item is null || item.LocationId != itemId || item.OwnerId != container.OwnerId))
        {
            throw new InvalidDataException("Choose items in this container to lock or unlock.");
        }
        List<InventoryChange> changes = [];
        foreach (var item in items)
        {
            var before = PilotInventoryStore.Read(catalog, item!);
            item!.FlagId = (int)(locked ? InventoryFlag.Locked : InventoryFlag.Unlocked);
            changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
        }
        world.Commit();
        return changes;
    }

    internal static int AddedFlag(WorldAction world, CatalogDbContext catalog, OwnedItem container) =>
        catalog.RequireItem(container.TypeId).GroupId != (int)PersonalAssetContainerGroup.AuditLogSecure ? (int)InventoryFlag.None
        : world.Set<SavedCargoContainer>().Find(container.Id)?.Configuration.HasFlag(CargoContainerConfiguration.LockAddedItems) == true
            ? (int)InventoryFlag.Locked
        : (int)InventoryFlag.Unlocked;
}
