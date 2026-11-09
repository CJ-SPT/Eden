using Eden.Gameplay.Inventory;
using Eden.Gameplay.Redemption;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Redemption;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

[Injectable]
internal sealed class PilotItemSpawningStore(WorldDatabase database, TimeProvider clock) : IPilotItemSpawning
{
    public ItemSpawnResult Create(long pilotId, long expectedShipId, ItemSpawnRequest request)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilotId);
        if (pilot is null || pilot.ActiveShipId != expectedShipId)
        {
            throw new InvalidDataException("The selected pilot or active ship changed. Select the pilot again.");
        }
        var type = catalog.FittingType(request.TypeId) ?? throw new InvalidDataException("Unknown inventory type.");
        ItemSpawnRules.Check(request, (InventoryCategory)type.CategoryId, pilot.ActiveShip.Position is null, type.GroupId);
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
        long location = cargo ? pilot.ActiveShipId : pilot.ActiveShip.StationId;
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
        InventoryChange change;
        try
        {
            change = InventoryPersistence.MergeOutput(world, catalog, pilotId, location, flag, request.TypeId, request.Quantity);
        }
        catch (OverflowException)
        {
            throw new InvalidDataException("The resulting item stack would exceed the supported quantity.");
        }
        world.SaveChanges();
        transaction.Commit();
        return new ItemSpawnResult.Inventory(change);
    }
}
