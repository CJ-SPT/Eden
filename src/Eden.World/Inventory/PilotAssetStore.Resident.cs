using Eden.Data.Catalog;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal sealed partial class PilotAssetStore
{
    private IReadOnlyList<PersonalAssetLocation> ResidentLocations(Eden.World.Runtime.LiveWorld live, long pilotId)
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

    private IReadOnlyList<InventoryItem> ResidentItems(Eden.World.Runtime.LiveWorld live, long pilotId, PersonalAssetQuery query)
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

    private static IQueryable<OwnedItem> AvailableItems(Eden.World.Runtime.Economy.EconomyTransaction world, long pilotId) =>
        world
            .Set<OwnedItem>()
            .AsNoTracking()
            .Where(i => i.OwnerId == pilotId)
            .Where(i => !world.Set<IndustryJob>().Any(j => j.BlueprintId == i.Id && j.Status == IndustryJobStatus.Installed));

    private static void CheckPilot(Eden.World.Runtime.Economy.EconomyTransaction world, long pilotId)
    {
        if (!world.Pilots.Any(p => p.Id == pilotId))
        {
            throw new InvalidDataException("Unknown pilot.");
        }
    }

    private static PersonalAssetLocation? Location(Eden.World.Runtime.Economy.EconomyTransaction world, CatalogDbContext catalog, long id)
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
