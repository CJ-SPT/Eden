using Eden.Data.Catalog;
using Eden.Data.World.Contracts;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Industry;
using Eden.World.Insurance;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

[Injectable]
internal sealed class PilotItemTrashStore(WorldDatabase database) : IPilotItemTrash
{
    // Build 3662168 evetypes.const.TYPE_LIST_NON_TRASHABLE_ITEMS.
    internal const long NonTrashableTypeList = 298;

    public IReadOnlyList<InventoryChange> Trash(long pilotId, int stationId, IReadOnlyList<long> itemIds)
    {
        ItemTrashRules.CheckSelection(itemIds);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (!world.Pilots.Any(p => p.Id == pilotId) || catalog.FindStation(stationId) is null)
        {
            throw new InvalidDataException("Choose personal assets at a known station.");
        }
        var restricted = Restrictions(catalog.Require(CatalogData.Client.Typelist, NonTrashableTypeList));
        var roots = itemIds.Select(id => Read(world, catalog, id)).ToArray();
        if (roots.Any(r => r.Item.LocationId != roots[0].Item.LocationId || r.Item.FlagId != roots[0].Item.FlagId))
        {
            throw new InvalidDataException("Select items in the same inventory hold.");
        }
        foreach (var root in roots)
        {
            CheckStation(world, catalog, pilotId, stationId, root);
        }

        List<TrashEntry> entries = [];
        HashSet<long> visited = [];
        Queue<TrashEntry> pending = new(roots);
        var selected = itemIds.ToHashSet();
        while (pending.TryDequeue(out var entry))
        {
            if (!visited.Add(entry.Item.Id))
            {
                throw new InvalidDataException("Invalid or overlapping inventory contents.");
            }
            ItemTrashRules.Check(
                new(
                    entry.Item,
                    pilotId,
                    entry.Ship is not null && world.Pilots.Any(p => p.ActiveShipId == entry.Item.Id),
                    Committed(world, entry.Item.Id),
                    restricted.Contains(entry.Item),
                    selected.Contains(entry.Item.Id)
                )
            );
            if (entry.Ship?.Position is not null)
            {
                throw new InvalidDataException("Ships in space cannot be trashed.");
            }
            entries.Add(entry);
            // Ignore escrow filters when discovering descendants: a hidden or foreign
            // child must reject the batch rather than survive as an orphan.
            var children = world.Set<OwnedItem>().IgnoreQueryFilters().Where(i => i.LocationId == entry.Item.Id).OrderBy(i => i.Id);
            foreach (var child in children.ToArray())
            {
                pending.Enqueue(new(PilotInventoryStore.Read(catalog, child), child, null));
            }
            var ships = world
                .Ships.IgnoreQueryFilters()
                .Include(s => s.Position)
                .Where(s => s.StoredInId == entry.Item.Id)
                .OrderBy(s => s.Id);
            foreach (var ship in ships.ToArray())
            {
                pending.Enqueue(new(PilotFleetStore.Read(catalog, ship), null, ship));
            }
        }

        List<InventoryChange> changes = [];
        // Remove descendants before parents, and notify from the same saved manifest.
        foreach (var entry in entries.AsEnumerable().Reverse())
        {
            if (entry.Ship is { } ship)
            {
                PilotInsuranceStore.VoidRemovedHull(world, ship.Id);
                world.Remove(ship);
            }
            else
            {
                world.Remove(entry.Stack!);
            }
            changes.Add(new(entry.Item with { Quantity = 0 }, entry.Item));
        }
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }

    private static TrashEntry Read(WorldDbContext world, CatalogDbContext catalog, long id)
    {
        if (world.Ships.IgnoreQueryFilters().Include(s => s.Position).SingleOrDefault(s => s.Id == id) is { } ship)
        {
            return new(PilotFleetStore.Read(catalog, ship), null, ship);
        }
        if (world.Set<OwnedItem>().IgnoreQueryFilters().SingleOrDefault(i => i.Id == id) is { } stack)
        {
            return new(PilotInventoryStore.Read(catalog, stack), stack, null);
        }
        throw new InvalidDataException("The requested item is unavailable.");
    }

    private static void CheckStation(WorldDbContext world, CatalogDbContext catalog, long pilotId, int stationId, TrashEntry entry)
    {
        HashSet<long> visited = [];
        while (true)
        {
            if (
                !visited.Add(entry.Item.Id)
                || entry.Item.OwnerId != pilotId
                || Committed(world, entry.Item.Id)
                || entry.Ship?.Position is not null
            )
            {
                throw new InvalidDataException("Use personal inventory stored at the requested station.");
            }
            if (entry.Item.LocationId == stationId && entry.Item.FlagId == (int)InventoryFlag.Hangar)
            {
                return;
            }
            if (
                !ShipHoldRules.IsHold(entry.Item.FlagId)
                && entry.Item.FlagId is not ((int)InventoryFlag.Unlocked or (int)InventoryFlag.Locked)
            )
            {
                throw new InvalidDataException("Use loose inventory in a station, ship hold or container.");
            }
            entry = Read(world, catalog, entry.Item.LocationId);
        }
    }

    private static bool Committed(WorldDbContext world, long id) =>
        world.Set<SavedContractEscrow>().Any(e => e.ItemId == id)
        || world.Set<SavedContract>().Any(c => c.PackageId == id && c.Status == ContractStatus.InProgress)
        || PilotIndustryStore.IsInstalled(world, id);

    private static ItemTrashTypeList Restrictions(ClientTypelist list) =>
        new(
            list.IncludedCategoryIDs?.ToHashSet() ?? [],
            list.IncludedGroupIDs?.ToHashSet() ?? [],
            list.IncludedTypeIDs?.ToHashSet() ?? [],
            list.ExcludedCategoryIDs?.ToHashSet() ?? [],
            list.ExcludedGroupIDs?.ToHashSet() ?? [],
            list.ExcludedTypeIDs?.ToHashSet() ?? []
        );

    private sealed record TrashEntry(InventoryItem Item, OwnedItem? Stack, Ship? Ship);
}
