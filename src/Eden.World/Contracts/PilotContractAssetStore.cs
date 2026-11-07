using Eden.Data.Catalog;
using Eden.Data.Industry;
using Eden.Data.Inventory;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Contracts;

[Injectable]
internal sealed class PilotContractAssetStore(WorldDatabase database) : IPilotContractAssets
{
    public IReadOnlyList<ContractAssetStation> Stations(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (!world.Pilots.Any(p => p.Id == pilotId))
        {
            throw new InvalidDataException("Unknown pilot.");
        }
        var items = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && i.FlagId == (int)InventoryFlag.Hangar)
            .Where(i => !world.Set<IndustryJob>().Any(j => j.BlueprintId == i.Id && j.Status == IndustryJobStatus.Installed))
            .GroupBy(i => i.LocationId)
            .Select(g => new { LocationId = g.Key, Count = g.Count() })
            .ToArray();
        // The global inventory includes the active docked ship. The native contract
        // wizard subtracts it from the current station's displayed item count.
        var ships = world
            .Ships.Where(s => s.OwnerId == pilotId && s.StoredInId == null && s.Position == null)
            .GroupBy(s => s.StationId)
            .Select(g => new ContractAssetStation(g.Key, g.Count()))
            .ToArray();
        var locations = items.Select(i => i.LocationId).Concat(ships.Select(s => (long)s.StationId)).ToArray();
        var stations = catalog.Set<Station>().Where(s => locations.Contains(s.Id)).Select(s => s.Id).ToHashSet();
        return items
            .Where(i => i.LocationId is > 0 and <= int.MaxValue && stations.Contains((int)i.LocationId))
            .Select(i => new ContractAssetStation((int)i.LocationId, i.Count))
            .Concat(ships.Where(s => stations.Contains(s.StationId)))
            .GroupBy(s => s.StationId)
            .Select(g => new ContractAssetStation(g.Key, g.Sum(s => s.ItemCount)))
            .OrderBy(s => s.StationId)
            .ToArray();
    }

    public IReadOnlyList<InventoryItem> Station(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        CheckStation(world, catalog, pilotId, stationId);
        return Assets(world, catalog, pilotId, stationId);
    }

    public IReadOnlyList<InventoryItem> Contents(long pilotId, int stationId, long itemId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        CheckStation(world, catalog, pilotId, stationId);
        CheckRoots(world, catalog, pilotId, stationId, [itemId]);
        return Contents(world, catalog, pilotId, itemId);
    }

    public IReadOnlyList<ContractContainerCount> Counts(long pilotId, int stationId, IReadOnlyList<long> itemIds)
    {
        ContractAssetRules.CheckSelection(itemIds);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        CheckStation(world, catalog, pilotId, stationId);
        CheckRoots(world, catalog, pilotId, stationId, itemIds);
        return itemIds.Select(id => new ContractContainerCount(id, Contents(world, catalog, pilotId, id).Count)).ToArray();
    }

    public IReadOnlyList<InventoryChange> Split(long pilotId, int stationId, long itemId, int quantity)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        CheckStation(world, catalog, pilotId, stationId);
        var stack = InventoryPersistence.OwnedStack(world, pilotId, itemId);
        if (
            stack is null
            || stack.LocationId != stationId
            || stack.FlagId != (int)InventoryFlag.Hangar
            || PilotIndustryStore.IsInstalled(world, itemId)
            || world.Set<OwnedItem>().Any(i => i.LocationId == itemId)
            || world.Ships.Any(s => s.StoredInId == itemId)
        )
        {
            throw new InvalidDataException("Split an available stack in your personal station hangar.");
        }
        var previous = PilotInventoryStore.Read(catalog, stack);
        var split = ContractAssetRules.Split(previous, quantity);
        stack.Quantity = split.Remainder;
        // Do not merge the new stack: the creation window selects the separated units.
        var separated = world
            .Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilotId,
                    TypeId = stack.TypeId,
                    LocationId = stationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = split.Quantity,
                    CrystalDamage = stack.CrystalDamage,
                }
            )
            .Entity;
        var changes = new InventoryChange[]
        {
            new(PilotInventoryStore.Read(catalog, stack), previous),
            new(PilotInventoryStore.Read(catalog, separated), null),
        };
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }

    private static void CheckStation(WorldDbContext world, CatalogDbContext catalog, long pilotId, int stationId)
    {
        // Contract preparation can inspect and split remote personal hangars.
        if (!world.Pilots.Any(p => p.Id == pilotId) || !catalog.Set<Station>().Any(s => s.Id == stationId))
        {
            throw new InvalidDataException("Unknown pilot or contract station.");
        }
    }

    internal static IReadOnlyList<InventoryItem> Assets(WorldDbContext world, CatalogDbContext catalog, long pilotId, int stationId)
    {
        var items = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && i.LocationId == stationId && i.FlagId == (int)InventoryFlag.Hangar)
            .Where(i => !world.Set<IndustryJob>().Any(j => j.BlueprintId == i.Id && j.Status == IndustryJobStatus.Installed))
            .AsEnumerable()
            .Select(i => PilotInventoryStore.Read(catalog, i));
        var ships = world
            .Ships.Include(s => s.Position)
            .Where(s => s.OwnerId == pilotId && s.StationId == stationId && s.StoredInId == null && s.Position == null)
            .Where(s => !world.Pilots.Any(p => p.ActiveShipId == s.Id))
            .AsEnumerable()
            .Select(s => PilotFleetStore.Read(catalog, s));
        return items.Concat(ships).OrderBy(i => i.Id).ToArray();
    }

    private static void CheckRoots(WorldDbContext world, CatalogDbContext catalog, long pilotId, int stationId, IReadOnlyList<long> itemIds)
    {
        ContractAssetRules.CheckSelection(itemIds);
        var available = Assets(world, catalog, pilotId, stationId).Select(i => i.Id).ToHashSet();
        if (itemIds.Any(id => !available.Contains(id)))
        {
            throw new InvalidDataException("The selected items are unavailable in your personal station hangar.");
        }
    }

    internal static IReadOnlyList<InventoryItem> Contents(WorldDbContext world, CatalogDbContext catalog, long pilotId, long root)
    {
        // Include fitted modules, their loaded charges and ships in bays. Keep the
        // original location/flag on every row so a later escrow transfer can retain the graph.
        List<InventoryItem> contents = [];
        HashSet<long> visited = [root];
        long[] parents = [root];
        while (parents.Length > 0)
        {
            var children = world
                .Set<OwnedItem>()
                .Where(i => i.OwnerId == pilotId && parents.Contains(i.LocationId))
                .AsEnumerable()
                .Select(i => PilotInventoryStore.Read(catalog, i))
                .Concat(
                    world
                        .Ships.Include(s => s.Position)
                        .Where(s =>
                            s.OwnerId == pilotId && s.Position == null && s.StoredInId != null && parents.Contains(s.StoredInId.Value)
                        )
                        .AsEnumerable()
                        .Select(s => PilotFleetStore.Read(catalog, s))
                )
                .OrderBy(i => i.Id)
                .ToArray();
            if (children.Any(i => !visited.Add(i.Id)))
            {
                throw new InvalidDataException("The selected container has invalid nested contents.");
            }
            contents.AddRange(children);
            parents = children.Select(i => i.Id).ToArray();
        }
        return contents.OrderBy(i => i.Id).ToArray();
    }
}
