using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Runtime;

namespace Eden.World.Propulsion;

internal sealed partial class PilotSpatialModuleStore
{
    public void Cancel(long pilotId, long shipId)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Gate cloak requires the resident world.");
        using var action = live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        if (action.Pilots.Find(pilotId)?.ActiveShipId != shipId)
        {
            throw new InvalidDataException("Cancel gate cloak on your active ship.");
        }
        GateCloakPersistence.Cancel(action, shipId);
        action.Commit();
    }

    public bool PreservesCloak(long pilotId, long shipId, long moduleId)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Gate cloak requires the resident world.");
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(world =>
            world.Pilots.Find(pilotId)?.ActiveShipId == shipId
            && world.Set<Eden.Data.World.Inventory.OwnedItem>().Find(moduleId) is { } item
            && item.OwnerId == pilotId
            && item.LocationId == shipId
            && item.Singleton
            && item.Online
            && InventoryDefinitions.IsFittedSlot(item.FlagId)
            && catalog.FittingAttributes(item.TypeId).GetValueOrDefault(DogmaAttributeId.CanActivateInGateCloak) > 0
        );
    }
}
