using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

// Build 3662168 shipConfigSvc and inventory tree access-button initialization.
// Personal storage works independently of the deferred shared-access role.
internal sealed record ShipSharingConfiguration : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["FleetHangar_AllowFleetAccess"] = false,
            ["FleetHangar_AllowCorpAccess"] = false,
            ["SMB_AllowFleetAccess"] = false,
            ["SMB_AllowCorpAccess"] = false,
        };
}
