using Eden.Data.World.Inventory;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime;

namespace Eden.World.Fittings;

internal sealed partial class PilotFittingStore : IPilotMultifit
{
    public MultifitResult FitShips(long pilotId, MultifitRequest request)
    {
        MultifitRules.Check(request);
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Multifit requires the resident world.");
        using var transaction = live.Economy.Begin(new(pilots: [pilotId]));
        var world = transaction;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        var fitting = request.Fitting;
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.DockedLocationId != fitting.SourceId)
        {
            throw new InvalidDataException("Multifit ships in your current station hangar while docked.");
        }
        CheckFittingService(world, catalog, pilot.ActiveShip, pilotId);
        var stacks = world
            .Filter<OwnedItem>(item =>
                item.OwnerId == pilotId && item.LocationId == fitting.SourceId && item.FlagId == (int)InventoryFlag.Hangar
            )
            .OrderBy(item => item.Id)
            .ToArray();
        var packages = stacks.Where(item => item.TypeId == fitting.ShipTypeId && !item.Singleton).ToArray();
        var available = stacks
            .GroupBy(item => item.TypeId)
            .ToDictionary(group => group.Key, group => group.Sum(item => (long)item.Quantity));
        available[fitting.ShipTypeId] = packages.Sum(item => (long)item.Quantity);
        var missing = MultifitRules
            .RequiredItems(request)
            .Select(item => new MissingFittingItem(item.TypeId, (int)Math.Max(0, item.Quantity - available.GetValueOrDefault(item.TypeId))))
            .Where(item => item.Quantity > 0)
            .OrderBy(item => item.TypeId)
            .ToArray();
        if (missing.Length > 0)
        {
            return new([], [], missing);
        }

        var sources = stacks
            .GroupBy(item => item.TypeId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<long>)group.Select(item => item.Id).ToArray());
        List<long> ships = [];
        Dictionary<long, InventoryChange> changes = [];
        int packageIndex = 0;
        for (int index = 0; index < request.Count; index++)
        {
            var package = packages[packageIndex];
            var assembly = PilotFleetStore.Assemble(world, catalog, pilotId, [package.Id], request.Name);
            // The last package is replaced by a Ship row with the same identity.
            if (world.Set<OwnedItem>().Find(package.Id) is null)
            {
                packageIndex++;
            }
            long shipId = assembly.ShipIds.Single();
            var ship = world.Ships.Find(shipId)!;
            Track(assembly.Changes);
            var result = Apply(world, catalog, pilot, ship, fitting with { ShipId = shipId, SourceItems = sources });
            if (result.Missing.Count > 0)
            {
                // Availability was checked under this owner; never publish a partly fitted batch.
                throw new InvalidDataException("The multifit inventory changed before the batch could be completed.");
            }
            Track(result.Changes);
            ships.Add(shipId);
        }
        transaction.Commit();
        return new(ships, changes.Values.ToArray(), []);

        void Track(IReadOnlyList<InventoryChange> updates)
        {
            foreach (var change in updates)
            {
                changes[change.Item.Id] = changes.TryGetValue(change.Item.Id, out var previous)
                    ? change with
                    {
                        Previous = previous.Previous,
                    }
                    : change;
            }
        }
    }
}
