using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

[Injectable]
internal sealed partial class PilotInventoryStackStore(WorldDatabase database, TimeProvider clock) : IPilotInventoryStacks
{
    private sealed record StackIdentity(int TypeId, double? VolumeOverride, double CrystalDamage);

    public IReadOnlyList<InventoryChange> Merge(
        long pilotId,
        long sourceLocationId,
        long destinationLocationId,
        IReadOnlyList<InventoryStackMerge> operations
    )
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is { } live)
        {
            return ResidentMerge(live, pilotId, sourceLocationId, destinationLocationId, operations);
        }
        InventoryStackRules.CheckMergeSelection(operations);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var ids = operations.SelectMany(operation => new[] { operation.SourceItemId, operation.TargetItemId }).Distinct().ToArray();
        var items = world.Set<OwnedItem>().Where(item => ids.Contains(item.Id)).ToDictionary(item => item.Id);
        if (items.Count != ids.Length || items.Values.Any(item => item.OwnerId != pilotId || !Accessible(item.LocationId, item.FlagId)))
        {
            throw new InvalidDataException("Merge your own stacks in the current station hangar or active ship hold.");
        }
        if (
            world.Set<OwnedItem>().Any(item => ids.Contains(item.LocationId))
            || world.Ships.Any(ship => ship.StoredInId != null && ids.Contains(ship.StoredInId.Value))
            || world.Set<IndustryJob>().Any(job => job.Status == IndustryJobStatus.Installed && ids.Contains(job.BlueprintId))
        )
        {
            throw new InvalidDataException("Occupied containers and installed blueprints cannot be merged.");
        }
        var before = items.ToDictionary(pair => pair.Key, pair => PilotInventoryStore.Read(catalog, pair.Value));
        HashSet<long> removedIds = [];
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        foreach (var operation in operations)
        {
            var source = items[operation.SourceItemId];
            var target = items[operation.TargetItemId];
            if (
                source.LocationId != sourceLocationId
                || target.LocationId != destinationLocationId
                || source.VolumeOverride != target.VolumeOverride
            )
            {
                throw new InvalidDataException("The selected stacks are unavailable in these inventories or have different metadata.");
            }
            var quantities = InventoryStackRules.Merge(
                PilotInventoryStore.Read(catalog, source),
                PilotInventoryStore.Read(catalog, target),
                operation.Quantity
            );
            OwnedItem consumed = source;
            if (source.LocationId != target.LocationId || source.FlagId != target.FlagId)
            {
                // Reuse ordinary hold, docking and capacity checks inside this transaction.
                // Intermediate split/move rows never become client-visible notifications.
                var moved = PilotInventoryStore.Move(
                    world,
                    catalog,
                    pilotId,
                    destinationLocationId,
                    new([source.Id], sourceLocationId, target.FlagId, operation.Quantity),
                    now
                );
                long movedId = moved
                    .Single(change => change.Item.LocationId == target.LocationId && change.Item.FlagId == target.FlagId)
                    .Item.Id;
                consumed = world.Set<OwnedItem>().Find(movedId)!;
            }
            else
            {
                source.Quantity = quantities.Source;
            }
            target.Quantity = quantities.Target;
            if (consumed != source || quantities.Source == 0)
            {
                removedIds.Add(consumed.Id);
                world.Remove(consumed);
            }
        }
        var changes = items
            .Values.Select(item => new InventoryChange(
                removedIds.Contains(item.Id) ? before[item.Id] with { Quantity = 0 } : PilotInventoryStore.Read(catalog, item),
                before[item.Id]
            ))
            .Where(change => change.Item != change.Previous)
            .ToArray();
        world.SaveChanges();
        transaction.Commit();
        return changes;

        bool Accessible(long location, int flag) =>
            location == pilot.ActiveShipId && ShipHoldRules.IsHold(flag)
            || location == pilot.ActiveShip.DockedLocationId && flag == (int)InventoryFlag.Hangar && pilot.ActiveShip.Position is null;
    }

    public IReadOnlyList<InventoryChange> StackAll(long pilotId, long locationId, int flagId)
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is { } live)
        {
            return ResidentStackAll(live, pilotId, locationId, flagId);
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        if (
            !(locationId == ship.DockedLocationId && flagId == (int)InventoryFlag.Hangar && ship.Position is null)
            && !(locationId == ship.Id && ShipHoldRules.IsHold(flagId))
        )
        {
            throw new InvalidDataException("Stack items in your current station hangar or active ship hold.");
        }
        var stacks = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && i.LocationId == locationId && i.FlagId == flagId && !i.Singleton && !i.Online)
            .OrderBy(i => i.Id)
            .ToArray();
        var ids = stacks.Select(i => i.Id).ToArray();
        var containers = world.Set<OwnedItem>().Where(i => ids.Contains(i.LocationId)).Select(i => i.LocationId).ToHashSet();
        var installed = world.Set<IndustryJob>().Where(j => j.Status == IndustryJobStatus.Installed).Select(j => j.BlueprintId).ToHashSet();
        List<InventoryChange> changes = [];
        foreach (
            var group in stacks
                .Where(i => !containers.Contains(i.Id) && !installed.Contains(i.Id))
                .GroupBy(i => new StackIdentity(i.TypeId, i.VolumeOverride, i.CrystalDamage))
                .Where(g => g.Count() > 1)
        )
        {
            var retained = group.First();
            var previous = PilotInventoryStore.Read(catalog, retained);
            retained.Quantity = InventoryStackRules.CombinedQuantity(group.Select(i => i.Quantity));
            changes.Add(new(PilotInventoryStore.Read(catalog, retained), previous));
            foreach (var removed in group.Skip(1))
            {
                var before = PilotInventoryStore.Read(catalog, removed);
                changes.Add(new(before with { Quantity = 0 }, before));
                world.Remove(removed);
            }
        }
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }
}
