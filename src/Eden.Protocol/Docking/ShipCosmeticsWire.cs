using Eden.Gameplay.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

internal sealed record ShipSkinLicenseReply(ShipSkinLicense License) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["skinID"] = License.SkinId,
                ["expires"] = License.Expires,
                ["isSingleUse"] = License.IsSingleUse,
            }
        );
}

internal sealed record ShipSkinLicenseActivated(int SkinId, long LicenseeId)
    : PilotNotification("OnSkinLicenseActivated", BroadcastCharacterId: LicenseeId)
{
    public override object?[] Arguments => [SkinId, LicenseeId];
}
