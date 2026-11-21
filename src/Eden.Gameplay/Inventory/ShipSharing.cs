using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Inventory;

public sealed record ShipSharingConfiguration(
    bool FleetHangarFleet = false,
    bool FleetHangarCorporation = false,
    bool MaintenanceFleet = false,
    bool MaintenanceCorporation = false
);

public sealed record ShipSharingChange(
    bool? FleetHangarFleet,
    bool? FleetHangarCorporation,
    bool? MaintenanceFleet,
    bool? MaintenanceCorporation
);

public sealed record SharedFleetHangar(InventoryItem Ship, IReadOnlyList<InventoryItem> Items, double Capacity, double Used);

public sealed record SharedShipBay(InventoryItem Ship, IReadOnlyList<InventoryItem> Vessels, double Capacity, double Used);

public interface IPilotShipSharing
{
    ShipSharingConfiguration? Configuration(long pilotId, long shipId);
    void Configure(long pilotId, long shipId, ShipSharingChange change);
    SharedFleetHangar? FleetHangar(long pilotId, long shipId);
    IReadOnlyList<InventoryChange>? MoveFleetHangar(long pilotId, long destination, InventoryMoveRequest request);
}

public interface IPilotShipMaintenance
{
    SharedShipBay? Bay(long pilotId, long shipId);
    ShipBoarding Store(long pilotId, long shipId, long hostShipId);
    ShipBoarding Board(long pilotId, long shipId, long hostShipId, long storedShipId);
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
