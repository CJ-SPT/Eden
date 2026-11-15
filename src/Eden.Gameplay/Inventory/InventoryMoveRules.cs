namespace Eden.Gameplay.Inventory;

public static class InventoryMoveRules
{
    public static bool CanUseShip(ShipInventoryAccess access, ShipStorageLocation ship) =>
        ship.OwnerId == access.PilotId
        && (
            ship.Id == access.ActiveShipId
            || access.Docked && ship.LocationId == access.StationId && ship.FlagId == (int)InventoryFlag.Hangar
        );

    public static bool CanUseItems(ShipInventoryAccess access, long location, int flag, ShipStorageLocation? ship) =>
        ship is not null
            ? ship.Id == location && CanUseShip(access, ship) && ShipHoldRules.IsHold(flag)
            : access.Docked && location == access.StationId && flag == (int)InventoryFlag.Hangar;
}
