namespace Eden.Gameplay.Inventory;

public sealed record ShipSharingConfiguration(bool FleetHangarFleet = false, bool FleetHangarCorporation = false);

public sealed record SharedFleetHangar(InventoryItem Ship, IReadOnlyList<InventoryItem> Items, double Capacity, double Used);

public interface IPilotShipSharing
{
    ShipSharingConfiguration? Configuration(long pilotId, long shipId);
    void ConfigureFleetHangar(long pilotId, long shipId, bool? fleet, bool? corporation);
    SharedFleetHangar? FleetHangar(long pilotId, long shipId);
    IReadOnlyList<InventoryChange>? MoveFleetHangar(long pilotId, long destination, InventoryMoveRequest request);
}

public interface IPilotMobileRefitting
{
    IReadOnlyList<InventoryChange> Fit(long pilotId, long shipId, InventoryMoveRequest request);
    IReadOnlyList<InventoryChange>? Unfit(long pilotId, long shipId, InventoryMoveRequest request);
    IReadOnlyList<InventoryChange> SetOnline(long pilotId, long shipId, long moduleId, bool online);
}

public static class ShipSharingRules
{
    // Build 3662168 invControllers.ShipFleetHangar and appConst.maxCargoContainerTransferDistance.
    public const double TransferRange = 2500;

    // Build 3662168 appConst.maxConfigureDistance.
    public const double FittingRange = 5000;
}
