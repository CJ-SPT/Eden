using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fittings;

internal sealed partial class PilotFittingStore
{
    public AppliedFitting Apply(long pilotId, ApplyFittingRequest request)
    {
        using WorldDbContext world = new(database.Path) { Participants = new(pilots: [pilotId], ships: [request.ShipId]) };
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
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
        CheckFittingService(world, ship, pilotId);

        long source = request.SourceId;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var sourceIds = request.SourceItems.Values.SelectMany(ids => ids).ToHashSet();
        var stacks = world
            .Set<OwnedItem>()
            .Where(item =>
                item.OwnerId == pilotId
                && item.LocationId == source
                && item.FlagId == (int)InventoryFlag.Hangar
                && sourceIds.Contains(item.Id)
            )
            .ToDictionary(item => item.Id);
        var rack = world
            .Set<OwnedItem>()
            .Where(item => item.OwnerId == pilotId && item.LocationId == ship.Id)
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
            var checkpoint = FittingChanges.Capture(world, catalog, pilot, now);
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
                        deferFittingChanges: true
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
                    batchCheckpoint: checkpoint
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
                    world.Set<SavedModuleCondition>().Find(module.Id) is null
                    || !ModuleConditionPersistence.Read(world, catalog, module).BurnedOut
                )
                {
                    module.Online = true;
                }
            }
            Track(FittingChanges.Finish(world, catalog, pilot, checkpoint, now));
        }

        foreach (var group in request.Supplies.GroupBy(item => (item.Flag, item.TypeId)))
        {
            int flag = (int)group.Key.Flag;
            int type = group.Key.TypeId;
            int target = group.Sum(item => item.Quantity);
            long existing = world
                .Set<OwnedItem>()
                .Where(item => item.OwnerId == pilotId && item.LocationId == ship.Id && item.FlagId == flag && item.TypeId == type)
                .Sum(item => (long)item.Quantity);
            int needed = (int)Math.Max(0, target - existing);
            foreach (long id in SourceStacks(type))
            {
                if (needed == 0)
                {
                    break;
                }
                int quantity = Math.Min(available[id], needed);
                Track(PilotInventoryStore.Move(world, catalog, pilotId, ship.Id, new([id], source, flag, quantity), now));
                available[id] -= quantity;
                needed -= quantity;
            }
            AddMissing(type, needed);
        }

        var changesToPublish = before
            .Select(pair => new InventoryChange(PilotInventoryStore.Read(catalog, world.Set<OwnedItem>().Find(pair.Key)!), pair.Value))
            .Where(change => change.Item != change.Previous)
            .ToArray();
        transaction.Commit();
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
