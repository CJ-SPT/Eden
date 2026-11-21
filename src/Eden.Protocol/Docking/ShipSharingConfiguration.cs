using Eden.Gameplay.Inventory;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

// Build 3662168 shipConfigSvc and inventory tree access-button initialization.
internal sealed record ShipSharingConfigurationWire(ShipSharingConfiguration? Settings = null) : BlueModel
{
    internal const string FleetHangarFleet = "FleetHangar_AllowFleetAccess";
    internal const string FleetHangarCorporation = "FleetHangar_AllowCorpAccess";
    internal const string MaintenanceFleet = "SMB_AllowFleetAccess";
    internal const string MaintenanceCorporation = "SMB_AllowCorpAccess";

    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            [FleetHangarFleet] = Settings?.FleetHangarFleet == true,
            [FleetHangarCorporation] = Settings?.FleetHangarCorporation == true,
            [MaintenanceFleet] = Settings?.MaintenanceFleet == true,
            [MaintenanceCorporation] = Settings?.MaintenanceCorporation == true,
        };
}
