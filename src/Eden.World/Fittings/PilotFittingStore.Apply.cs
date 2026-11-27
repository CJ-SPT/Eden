using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Fittings;

internal sealed partial class PilotFittingStore
{
    // Build 3662168 evetypes.const.TYPE_LIST_CANNOT_BE_UNFITTED.
    private const int CannotBeUnfittedTypeList = 338;

    public IReadOnlyList<InventoryChange> Strip(long pilotId, long shipId)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        var ship = world.Ships.Find(shipId) ?? throw new InvalidDataException("Unknown fitting ship.");
        RequireFittingShip(pilot, ship);
        CheckFittingService(world, catalog, ship, pilotId);

        var modules = PilotInventoryStore
            .List(world, catalog, pilotId, shipId)
            .Where(item => InventoryDefinitions.IsFittedSlot(item.FlagId) && !InventoryDefinitions.IsRigSlot(item.FlagId))
            .ToArray();
        if (modules.Length == 0)
        {
            return [];
        }
        if (
            catalog.Read(CatalogData.Client.Typelist, CannotBeUnfittedTypeList) is not null
            && modules.Any(CatalogTypeLists.Read(catalog, CannotBeUnfittedTypeList).Contains)
        )
        {
            throw new InvalidDataException("This ship has equipment that cannot be unfitted.");
        }

        var changes = PilotInventoryStore.Move(
            world,
            catalog,
            pilotId,
            ship.DockedLocationId,
            new(modules.Select(module => module.Id).ToArray(), shipId, (int)InventoryFlag.Hangar, null),
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            fittingShip: ship
        );
        transaction.Commit();
        return changes;
    }

    public AppliedFitting Apply(long pilotId, ApplyFittingRequest request)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [request.ShipId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Filter<Pilot>(p => p.Id == pilotId).Single();
        var ship = pilot.ActiveShip;
        if (
            ship.OwnerId != pilotId
            || ship.Position is not null
            || ship.Id != request.ShipId
            || ship.TypeId != request.ShipTypeId
            || ship.DockedLocationId != request.SourceId
        )
        {
            throw new InvalidDataException("Apply the fitting to your docked active ship of the matching type.");
        }
        var result = Apply(world, catalog, pilot, ship, request);
        transaction.Commit();
        return result;
    }

    internal AppliedFitting Apply(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        Ship ship,
        ApplyFittingRequest request,
        IReadOnlySet<int>? initiallyOnline = null
    )
    {
        long pilotId = pilot.Id;
        CheckFittingService(world, catalog, ship, pilotId);

        long source = request.SourceId;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var sourceIds = request.SourceItems.Values.SelectMany(ids => ids).ToHashSet();
        var stacks = world
            .Filter<OwnedItem>(item =>
                item.OwnerId == pilotId
                && item.LocationId == source
                && item.FlagId == (int)InventoryFlag.Hangar
                && sourceIds.Contains(item.Id)
            )
            .ToDictionary(item => item.Id);
        var rack = world
            .Filter<OwnedItem>(item => item.OwnerId == pilotId && item.LocationId == ship.Id)
            .AsEnumerable()
            .Where(item => InventoryDefinitions.IsFittedSlot(item.FlagId))
            .ToDictionary(item => item.FlagId);
        var available = stacks.Values.ToDictionary(item => item.Id, item => item.Quantity);
        Dictionary<int, int> missing = [];
        List<SlotPlacement> placements = [];
        foreach (var slot in request.Slots)
        {
            bool rig = InventoryDefinitions.IsRigSlot(slot.FlagId);
            var current = rack.GetValueOrDefault(slot.FlagId);
            if (current?.TypeId == slot.TypeId || rig && (!request.FitRigs || current is not null))
            {
                continue;
            }
            long id = SourceStacks(slot.TypeId).FirstOrDefault();
            if (id == 0 || !InventoryDefinitions.IsFittedSlot(slot.FlagId))
            {
                AddMissing(slot.TypeId, 1);
                continue;
            }
            available[id]--;
            placements.Add(new(slot.FlagId, id, current?.Id));
        }

        Dictionary<long, InventoryItem?> before = [];
        if (placements.Count > 0)
        {
            var checkpoint = FittingChanges.Capture(world, catalog, pilot, now, ship);
            long[] displaced = placements.Select(p => p.DisplacedId).OfType<long>().ToArray();
            if (displaced.Length > 0)
            {
                Track(
                    PilotInventoryStore.Move(
                        world,
                        catalog,
                        pilotId,
                        source,
                        new(displaced, ship.Id, (int)InventoryFlag.Hangar, null),
                        now,
                        deferFittingChanges: true,
                        fittingShip: ship
                    )
                );
            }
            List<OwnedItem> fitted = [];
            foreach (var placement in placements)
            {
                var changes = Fit(
                    world,
                    catalog,
                    pilotId,
                    [placement.SourceId],
                    source,
                    placement.FlagId,
                    now,
                    batchCheckpoint: checkpoint,
                    fittingShip: ship
                );
                Track(changes);
                if (!InventoryDefinitions.IsRigSlot(placement.FlagId))
                {
                    var module = changes.Single(change => change.Item.FlagId == placement.FlagId).Item;
                    fitted.Add(world.Set<OwnedItem>().Find(module.Id)!);
                }
            }
            // Evaluate all new online effects together so fitting bonuses apply to the full rack.
            foreach (var module in fitted)
            {
                if (
                    (initiallyOnline is null || initiallyOnline.Contains(module.FlagId))
                    && (
                        world.Set<SavedModuleCondition>().Find(module.Id) is null
                        || !ModuleConditionPersistence.Read(world, catalog, module).BurnedOut
                    )
                )
                {
                    module.Online = true;
                }
            }
            Track(FittingChanges.Finish(world, catalog, pilot, checkpoint, now, ship));
        }

        foreach (var group in request.Supplies.GroupBy(item => (item.Flag, item.TypeId)))
        {
            int flag = (int)group.Key.Flag;
            int type = group.Key.TypeId;
            int target = group.Sum(item => item.Quantity);
            long existing = world
                .Filter<OwnedItem>(item =>
                    item.OwnerId == pilotId && item.LocationId == ship.Id && item.FlagId == flag && item.TypeId == type
                )
                .Sum(item => (long)item.Quantity);
            int needed = (int)Math.Max(0, target - existing);
            foreach (long id in SourceStacks(type))
            {
                if (needed == 0)
                {
                    break;
                }
                int quantity = Math.Min(available[id], needed);
                Track(
                    PilotInventoryStore.Move(world, catalog, pilotId, ship.Id, new([id], source, flag, quantity), now, fittingShip: ship)
                );
                available[id] -= quantity;
                needed -= quantity;
            }
            AddMissing(type, needed);
        }

        var changesToPublish = before
            .Select(pair => new InventoryChange(PilotInventoryStore.Read(catalog, world.Set<OwnedItem>().Find(pair.Key)!), pair.Value))
            .Where(change => change.Item != change.Previous)
            .ToArray();
        return new(
            changesToPublish,
            missing.OrderBy(pair => pair.Key).Select(pair => new MissingFittingItem(pair.Key, pair.Value)).ToArray()
        );

        IEnumerable<long> SourceStacks(int type) =>
            request.SourceItems.TryGetValue(type, out var ids)
                ? ids.Where(id => available.GetValueOrDefault(id) > 0 && stacks[id].TypeId == type)
                : [];

        void Track(IReadOnlyList<InventoryChange> changes)
        {
            foreach (var change in changes)
            {
                before.TryAdd(change.Item.Id, change.Previous);
            }
        }

        void AddMissing(int type, int quantity)
        {
            if (quantity > 0)
            {
                missing[type] = checked(missing.GetValueOrDefault(type) + quantity);
            }
        }
    }

    private sealed record SlotPlacement(int FlagId, long SourceId, long? DisplacedId);
}
