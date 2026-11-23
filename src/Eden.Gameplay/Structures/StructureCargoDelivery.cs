using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Structures;

public interface IStructureCargoDelivery
{
    IReadOnlyList<InventoryChange> DropOff(long pilotId, long shipId, long structureId, IReadOnlyList<long> itemIds);
}

public static class StructureCargoDeliveryRules
{
    // Pinned build 3662168 inventorycommon.const.cargoHoldsValidForDeliveryService.
    public static bool CanDropOffFrom(int flag) =>
        (InventoryFlag)flag
            is InventoryFlag.Cargo
                or InventoryFlag.ShipMaintenanceBay
                or InventoryFlag.FuelBay
                or InventoryFlag.GeneralMiningHold
                or InventoryFlag.GasHold
                or InventoryFlag.MineralHold
                or InventoryFlag.AmmoHold
                or InventoryFlag.CommandCenterHold
                or InventoryFlag.PlanetaryCommoditiesHold
                or InventoryFlag.QuafeBay
                or InventoryFlag.FleetHangar
                or InventoryFlag.CorpseBay
                or InventoryFlag.BoosterHold
                or InventoryFlag.SubsystemHold
                or InventoryFlag.IceHold
                or InventoryFlag.AsteroidHold
                or InventoryFlag.MobileDepotHold
                or InventoryFlag.InfrastructureHold
                or InventoryFlag.ExpeditionHold;
}
