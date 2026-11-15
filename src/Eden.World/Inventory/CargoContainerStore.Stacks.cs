using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Industry;

namespace Eden.World.Inventory;

internal sealed partial class CargoContainerStore
{
    private sealed record ContainerStackIdentity(int TypeId, int FlagId, double? VolumeOverride, double CrystalDamage);

    public IReadOnlyList<InventoryChange> StackAll(long pilotId, long itemId, int? flagId, CargoContainerAccess access)
    {
        if (flagId is not (null or (int)InventoryFlag.None or (int)InventoryFlag.Unlocked))
        {
            throw new InvalidDataException("Stack unlocked container contents.");
        }
        using var world = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var container = Require(world, catalog, pilotId, itemId);
        RequireOpen(world, catalog, container, access);
        if (container.OwnerId != pilotId)
        {
            throw new InvalidDataException("Stack contents of your own container.");
        }
        var stacks = world
            .Set<OwnedItem>()
            .Where(item => item.LocationId == itemId && item.OwnerId == pilotId && !item.Singleton && !item.Online)
            .OrderBy(item => item.Id)
            .ToArray()
            .Where(item => Unlocked(item) && (!flagId.HasValue || item.FlagId == flagId) && !PilotIndustryStore.IsInstalled(world, item.Id))
            .GroupBy(item => new ContainerStackIdentity(item.TypeId, item.FlagId, item.VolumeOverride, item.CrystalDamage));
        List<InventoryChange> changes = [];
        foreach (var group in stacks.Where(group => group.Count() > 1))
        {
            var retained = group.First();
            var before = PilotInventoryStore.Read(catalog, retained);
            retained.Quantity = InventoryStackRules.CombinedQuantity(group.Select(item => item.Quantity));
            changes.Add(new(PilotInventoryStore.Read(catalog, retained), before));
            foreach (var removed in group.Skip(1))
            {
                var previous = PilotInventoryStore.Read(catalog, removed);
                changes.Add(new(previous with { Quantity = 0 }, previous));
                world.Remove(removed);
            }
        }
        world.Commit();
        return changes;
    }

    public IReadOnlyList<InventoryChange> Merge(
        long pilotId,
        long itemId,
        IReadOnlyList<InventoryStackMerge> operations,
        CargoContainerAccess access
    )
    {
        InventoryStackRules.CheckMergeSelection(operations);
        using var world = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var container = Require(world, catalog, pilotId, itemId);
        RequireOpen(world, catalog, container, access);
        var ids = operations.SelectMany(operation => new[] { operation.SourceItemId, operation.TargetItemId }).Distinct();
        var items = ids.ToDictionary(
            id => id,
            id => world.Set<OwnedItem>().Find(id) ?? throw new InvalidDataException("A selected stack is unavailable.")
        );
        if (
            container.OwnerId != pilotId
            || items.Values.Any(item =>
                item.OwnerId != pilotId || item.LocationId != itemId || !Unlocked(item) || PilotIndustryStore.IsInstalled(world, item.Id)
            )
        )
        {
            throw new InvalidDataException("Merge your unlocked stacks within this container.");
        }
        var before = items.ToDictionary(pair => pair.Key, pair => PilotInventoryStore.Read(catalog, pair.Value));
        foreach (var operation in operations)
        {
            var source = items[operation.SourceItemId];
            var target = items[operation.TargetItemId];
            if (source.FlagId != target.FlagId || source.VolumeOverride != target.VolumeOverride)
            {
                throw new InvalidDataException("Choose stacks with matching flags and metadata.");
            }
            var quantities = InventoryStackRules.Merge(
                PilotInventoryStore.Read(catalog, source),
                PilotInventoryStore.Read(catalog, target),
                operation.Quantity
            );
            source.Quantity = quantities.Source;
            target.Quantity = quantities.Target;
            if (source.Quantity == 0)
            {
                world.Remove(source);
            }
        }
        var changes = items
            .Values.Select(item => new InventoryChange(PilotInventoryStore.Read(catalog, item), before[item.Id]))
            .Where(change => change.Item != change.Previous)
            .ToArray();
        world.Commit();
        return changes;
    }

    internal static bool Unlocked(OwnedItem item) => item.FlagId is (int)InventoryFlag.None or (int)InventoryFlag.Unlocked;
}
