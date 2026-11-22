using Eden.Gameplay.Inventory;
using Eden.Gameplay.Redemption;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Redemption;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Inventory;

[Injectable]
internal sealed class PilotItemSpawningStore(WorldDatabase database, TimeProvider clock) : IPilotItemSpawning
{
    public IReadOnlyList<InventoryChange> CreateHangarBatch(long pilotId, long expectedShipId, IReadOnlyList<HangarSpawnStack> stacks)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Batch creation requires a running world.");
        using var world = live.Economy.Begin(new(pilots: [pilotId], ships: [expectedShipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var ship = world.Pilots.Find(pilotId)?.ActiveShip;
        if (ship is null || ship.Id != expectedShipId || ship.Position is not null || ship.StoredInId.HasValue)
        {
            throw new InvalidDataException("Dock before creating missing fitting items.");
        }
        List<InventoryChange> changes = [];
        foreach (var stack in stacks)
        {
            var type = catalog.FittingType(stack.TypeId) ?? throw new InvalidDataException("Unknown inventory type.");
            ItemSpawnRules.Check(
                new(type.Id, stack.Quantity, ItemSpawnDestination.StationHangar),
                (InventoryCategory)type.CategoryId,
                docked: true,
                type.GroupId
            );
            if (ItemSpawnRules.AutomaticReward(type.Id))
            {
                throw new InvalidDataException("This type cannot be created by the fitting tool.");
            }
            changes.Add(Merge(world, catalog, pilotId, ship.DockedLocationId, (int)InventoryFlag.Hangar, type.Id, stack.Quantity));
        }
        world.Commit();
        return changes;
    }

    public ItemSpawnResult Create(long pilotId, long expectedShipId, ItemSpawnRequest request)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [expectedShipId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId);
        if (pilot is null || pilot.ActiveShipId != expectedShipId)
        {
            throw new InvalidDataException("The selected pilot or active ship changed. Select the pilot again.");
        }
        var type = catalog.FittingType(request.TypeId) ?? throw new InvalidDataException("Unknown inventory type.");
        ItemSpawnRules.Check(request, (InventoryCategory)type.CategoryId, pilot.ActiveShip.Position is null, type.GroupId);
        if (request.ExpectedHangarId is long hangarId && hangarId != pilot.ActiveShip.DockedLocationId)
        {
            throw new InvalidDataException("The specified destination must be your current docked location.");
        }
        if (ItemSpawnRules.AutomaticReward(request.TypeId))
        {
            var token = AccountRedemptionStore.Grant(
                world,
                catalog,
                new RedeemGrant(pilot.AccountId, $"developer/{Guid.NewGuid():N}", request.TypeId, request.Quantity, type.Name),
                clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
            );
            transaction.Commit();
            return new ItemSpawnResult.Redemption(token);
        }
        bool cargo = request.Destination == ItemSpawnDestination.ShipCargo;
        long location = cargo ? pilot.ActiveShipId : pilot.ActiveShip.DockedLocationId;
        int flag = (int)(cargo ? InventoryFlag.Cargo : InventoryFlag.Hangar);
        if (cargo)
        {
            ItemSpawnRules.CheckCapacity(
                PilotInventoryStore.Volume(catalog, request.TypeId),
                request.Quantity,
                InventoryPersistence.UsedVolume(world, catalog, pilotId, location, flag),
                PilotInventoryStore.HoldCapacity(world, catalog, pilotId, location, flag)
            );
        }
        var change = Merge(world, catalog, pilotId, location, flag, request.TypeId, request.Quantity);
        world.SaveChanges();
        transaction.Commit();
        return new ItemSpawnResult.Inventory(change);
    }

    private static InventoryChange Merge(
        WorldAction world,
        CatalogDbContext catalog,
        long pilot,
        long location,
        int flag,
        int type,
        int quantity
    )
    {
        try
        {
            return InventoryPersistence.MergeOutput(world, catalog, pilot, location, flag, type, quantity);
        }
        catch (OverflowException)
        {
            throw new InvalidDataException("The resulting item stack would exceed the supported quantity.");
        }
    }
}
