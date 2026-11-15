using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Industry;

internal sealed partial class PilotIndustryStore
{
    public IReadOnlyList<IndustryInventoryLocation> Locations(long pilotId, long stationId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect(
            database,
            world =>
            {
                var station = CurrentStation(world, catalog, pilotId, stationId, null);
                List<IndustryInventoryLocation> locations =
                [
                    new(stationId, station.TypeId, pilotId, (int)InventoryFlag.Hangar, station.SolarSystemId),
                ];
                locations.AddRange(
                    world
                        .Set<OwnedItem>()
                        .Where(item =>
                            item.OwnerId == pilotId
                            && item.LocationId == stationId
                            && item.FlagId == (int)InventoryFlag.Hangar
                            && item.Singleton
                        )
                        .OrderBy(item => item.Id)
                        .ToArray()
                        .Where(item => CargoContainerAssemblyRules.IsContainer(PilotInventoryStore.Read(catalog, item)))
                        .Select(item => new IndustryInventoryLocation(
                            item.Id,
                            item.TypeId,
                            pilotId,
                            (int)InventoryFlag.Hangar,
                            station.SolarSystemId
                        ))
                );
                return locations.ToArray();
            }
        );
    }

    private static long? BlueprintStation(WorldAction world, CatalogDbContext catalog, OwnedItem item)
    {
        if (item.FlagId == (int)InventoryFlag.Hangar)
        {
            return item.LocationId;
        }
        var container = world.Set<OwnedItem>().Find(item.LocationId);
        return
            container is { Singleton: true }
            && container.OwnerId == item.OwnerId
            && container.FlagId == (int)InventoryFlag.Hangar
            && CargoContainerAssemblyRules.IsContainer(PilotInventoryStore.Read(catalog, container))
            && item.FlagId is (int)InventoryFlag.None or (int)InventoryFlag.Unlocked or (int)InventoryFlag.Locked
            ? container.LocationId
            : null;
    }

    private static IndustryStorage Storage(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long stationId,
        IndustryStorage? selected
    )
    {
        selected ??= new(stationId, (int)InventoryFlag.Hangar);
        if (selected.Id == stationId && selected.FlagId == (int)InventoryFlag.Hangar)
        {
            return selected;
        }
        var container = CargoContainerStore.Require(world, catalog, pilotId, selected.Id);
        if (
            container.OwnerId != pilotId
            || container.LocationId != stationId
            || container.FlagId != (int)InventoryFlag.Hangar
            || selected.FlagId != (int)InventoryFlag.Hangar
        )
        {
            throw new InvalidDataException("Choose a personal container in this facility's Item Hangar.");
        }
        CargoContainerStore.RequireOpen(world, catalog, container, selected.Access ?? new());
        // Industry locations carry their parent hangar flag. Container contents use None/Unlocked instead.
        return selected with { FlagId = (int)InventoryFlag.None };
    }

    private static void CheckBlueprintContainer(WorldAction world, CatalogDbContext catalog, OwnedItem item, IndustryRequest request)
    {
        if (item.FlagId == (int)InventoryFlag.Hangar)
        {
            return;
        }
        _ = Storage(
            world,
            catalog,
            item.OwnerId,
            request.FacilityId,
            new(item.LocationId, (int)InventoryFlag.Hangar, request.BlueprintAccess)
        );
        if (!CargoContainerStore.Unlocked(item))
        {
            throw new InvalidDataException("Unlock the blueprint before starting an industry job.");
        }
    }

    private static bool InStorage(OwnedItem item, IndustryStorage storage) =>
        item.LocationId == storage.Id
        && (storage.FlagId == (int)InventoryFlag.Hangar ? item.FlagId == (int)InventoryFlag.Hangar : CargoContainerStore.Unlocked(item));

    private static void SetJobOutput(WorldAction world, CatalogDbContext catalog, IndustryJob job, IndustryRequest request)
    {
        var storage = Storage(world, catalog, job.PilotId, job.StationId, request.Output);
        job.OutputLocationId = storage.Id;
        job.OutputFlagId =
            storage.Id == job.StationId
                ? (int)InventoryFlag.Hangar
                : CargoContainerStore.AddedFlag(world, catalog, world.Set<OwnedItem>().Find(storage.Id)!);
    }

    private static void CheckOutputCapacity(WorldAction world, CatalogDbContext catalog, IndustryJob job)
    {
        if (
            job.Activity is IndustryActivity.ResearchMaterial or IndustryActivity.ResearchTime
            || job.OutputLocationId is not long location
            || location == job.StationId
        )
        {
            return;
        }
        var container = world.Set<OwnedItem>().Find(location);
        if (
            container is not { Singleton: true }
            || container.OwnerId != job.PilotId
            || container.LocationId != job.StationId
            || container.FlagId != (int)InventoryFlag.Hangar
            || !CargoContainerAssemblyRules.IsContainer(PilotInventoryStore.Read(catalog, container))
        )
        {
            throw new InvalidDataException("Return the output container to this facility before delivering the job.");
        }
        double used = PilotInventoryStore.List(world, catalog, job.PilotId, location).Sum(item => item.Volume * item.Quantity);
        int quantity = job.Activity == IndustryActivity.Invention ? job.SuccessfulRuns ?? 0 : job.OutputQuantity;
        bool singleton = job.Activity is IndustryActivity.Copying or IndustryActivity.Invention;
        double required = PilotInventoryStore.Volume(catalog, job.ProductTypeId, singleton) * quantity;
        double available = (catalog.RequireItem(container.TypeId).Capacity ?? 0) - used;
        if (required > available + InventoryDefinitions.ResourceTolerance)
        {
            throw new CargoCapacityException(Math.Max(0, available), required);
        }
    }
}
