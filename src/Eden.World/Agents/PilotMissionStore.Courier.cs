using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static InventoryChange CreateCourierCargo(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int stationId,
        MissionProfile profile
    )
    {
        if (!profile.CargoSingleton)
        {
            return InventoryPersistence.MergeOutput(
                world,
                catalog,
                pilotId,
                stationId,
                (int)InventoryFlag.Hangar,
                profile.CargoType,
                profile.CargoQuantity
            );
        }
        // The admitted catalog singleton objectives contain one indivisible item.
        // Give each acceptance its own identity, even beside another item of this type.
        var item = world
            .Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilotId,
                    LocationId = stationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    TypeId = profile.CargoType,
                    Quantity = 1,
                    Singleton = true,
                }
            )
            .Entity;
        return new(PilotInventoryStore.Read(catalog, item), null);
    }
}
