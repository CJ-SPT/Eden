using Eden.Data.Catalog;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal sealed partial class PilotAssetStore
{
    public PersonalAssetWorth Worth(long pilotId)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Asset worth requires a resident world.");
        using CatalogDbContext catalog = new(database.CatalogPath);
        var quantities = live.Economy.Inspect(world =>
        {
            CheckPilot(world, pilotId);
            var holdings = AvailableItems(world, pilotId)
                .Select(i => new PersonalAssetHolding(i.Id, i.LocationId, i.TypeId, i.Quantity, i.Singleton))
                .Concat(
                    world
                        .Ships.Where(s => s.OwnerId == pilotId && s.Position == null)
                        .Select(s => new PersonalAssetHolding(s.Id, s.StoredInId ?? s.StructureId ?? s.StationId, s.TypeId, 1, true))
                )
                .ToArray();
            var docked = holdings.Select(i => i.LocationId).Distinct().Where(id => Location(world, catalog, id) is not null).ToHashSet();
            return PersonalAssetRules.DockedQuantities(holdings, docked);
        });
        var estimates = MarketValuations.ReferencePrices(catalog);
        var pricedTypes = quantities.Keys.Where(id =>
            id != CourierRules.PackageType && catalog.FittingType(id) is { CategoryId: not (int)InventoryCategory.Blueprint }
        );
        Dictionary<int, decimal> prices = [];
        foreach (int id in pricedTypes)
        {
            if (estimates.TryGetValue(id, out decimal estimate))
            {
                prices.Add(id, estimate);
            }
            else if (catalog.Read(CatalogData.Client.Types, (long)id)?.BasePrice is > 0 and <= (double)long.MaxValue and var basePrice)
            {
                prices.Add(id, (decimal)basePrice);
            }
        }
        return PersonalAssetRules.EstimateWorth(quantities, prices);
    }

    public IReadOnlyList<PersonalAssetName> StationItemNames(long pilotId, IReadOnlySet<long> itemIds)
    {
        if (itemIds.Count == 0 || LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }
        return live.Economy.Inspect<IReadOnlyList<PersonalAssetName>>(world =>
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            List<PersonalAssetName> names = [];
            var items = world.Set<OwnedItem>();
            foreach (long id in itemIds)
            {
                if (world.Ships.Find(id) is { Position: null, StoredInId: null } ship)
                {
                    if (ship.OwnerId == pilotId && Location(world, catalog, ship.DockedLocationId) is not null)
                    {
                        names.Add(new(ship.Id, ship.Name));
                    }
                    continue;
                }
                if (
                    items.Find(id) is { Singleton: true, FlagId: (int)InventoryFlag.Hangar } item
                    && item.OwnerId == pilotId
                    && catalog.FittingType(item.TypeId) is { } type
                    && Enum.IsDefined((PersonalAssetContainerGroup)type.GroupId)
                    && Location(world, catalog, item.LocationId) is not null
                )
                {
                    names.Add(new(item.Id, world.Set<SavedCargoContainer>().Find(item.Id)?.Name));
                }
            }
            return names;
        });
    }

    private IReadOnlyList<PersonalAssetLocation> ResidentLocations(LiveWorld live, long pilotId)
    {
        using var world = live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        CheckPilot(world, pilotId);
        var items = AvailableItems(world, pilotId)
            .Where(i => i.FlagId == (int)InventoryFlag.Hangar)
            .GroupBy(i => i.LocationId)
            .Select(g => new AssetCount(g.Key, g.Count()))
            .ToArray();
        // Include the active docked ship: the contract wizard subtracts it locally.
        var ships = world
            .Ships.Where(s => s.OwnerId == pilotId && s.StoredInId == null && s.Position == null)
            .GroupBy(s => s.StructureId ?? (long)s.StationId)
            .Select(g => new AssetCount(g.Key, g.Count()))
            .ToArray();
        return items
            .Concat(ships)
            .GroupBy(s => s.LocationId)
            .Select(g => new { Station = Location(world, catalog, g.Key), Count = g.Sum(s => s.Count) })
            .Where(s => s.Station is not null)
            .Select(s => s.Station! with { ItemCount = s.Count })
            .OrderBy(s => s.StationId)
            .ToArray();
    }

    private IReadOnlyList<InventoryItem> ResidentItems(LiveWorld live, long pilotId, PersonalAssetQuery query)
    {
        using var world = live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        CheckPilot(world, pilotId);
        if (query.StationId is long station && Location(world, catalog, station) is null)
        {
            throw new InvalidDataException("Choose a valid personal asset station.");
        }
        var stacks = AvailableItems(world, pilotId);
        var hulls = world.Ships.AsNoTracking().Include(s => s.Position).Where(s => s.OwnerId == pilotId && s.Position == null);
        if (!query.IncludeContainers)
        {
            stacks = stacks.Where(i => i.FlagId == (int)InventoryFlag.Hangar);
            hulls = hulls.Where(s => s.StoredInId == null);
        }
        if (query.StationId is long location && !query.IncludeContainers)
        {
            stacks = stacks.Where(i => i.LocationId == location);
            hulls = hulls.Where(s => (s.StructureId ?? (long)s.StationId) == location);
        }
        var items = stacks
            .AsEnumerable()
            .Select(i => PilotInventoryStore.Read(catalog, i))
            .Concat(hulls.AsEnumerable().Select(s => PilotFleetStore.Read(catalog, s)))
            .ToArray();
        var stations = items
            .Select(i => i.LocationId)
            .Distinct()
            .Where(id => (!query.StationId.HasValue || id == query.StationId) && Location(world, catalog, id) is not null)
            .ToHashSet();
        return PersonalAssetRules.Select(items, stations, query.IncludeContainers);
    }

    private static IQueryable<OwnedItem> AvailableItems(Eden.World.Runtime.Economy.WorldAction world, long pilotId) =>
        world
            .Set<OwnedItem>()
            .AsNoTracking()
            .Where(i => i.OwnerId == pilotId)
            .Where(i => !world.Set<IndustryJob>().Any(j => j.BlueprintId == i.Id && j.Status == IndustryJobStatus.Installed));

    private static void CheckPilot(Eden.World.Runtime.Economy.WorldAction world, long pilotId)
    {
        if (!world.Pilots.Any(p => p.Id == pilotId))
        {
            throw new InvalidDataException("Unknown pilot.");
        }
    }

    private static PersonalAssetLocation? Location(Eden.World.Runtime.Economy.WorldAction world, CatalogDbContext catalog, long id)
    {
        if (catalog.FindStation(id) is { } station)
        {
            return new(station.Id, station.SolarSystemId, station.TypeId, 0);
        }
        var structure = world.Structure(id);
        return structure is { State: not Eden.Gameplay.Structures.StructureState.Destroyed }
            ? new(structure.Id, structure.SystemId, structure.TypeId, 0)
            : null;
    }
}
