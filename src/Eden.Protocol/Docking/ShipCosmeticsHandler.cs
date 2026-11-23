using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class ShipCosmeticsHandler(IPilotShipSkinLicenses licenses) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        // Build 3662168's two remote collection reads use "Licenced".
        if (
            service != "shipCosmeticsMgr"
            || method is not ("GetLicencedSkins" or "GetLicencedSkinsForShipType" or "GetFirstPartySkinData" or "ActivateSkinLicense")
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Choose valid pilot and skin information.");
        }

        long pilotId = context.Pilot.PilotId;
        try
        {
            return (method, args) switch
            {
                ("GetLicencedSkins", []) => List(pilotId),
                ("GetLicencedSkinsForShipType", [long licenseeId, long typeId])
                    when licenseeId == pilotId && typeId is > 0 and <= int.MaxValue => List(pilotId, (int)typeId),
                // Applied skins may be previewed by another pilot.
                ("GetFirstPartySkinData", [long licenseeId, long skinId]) when licenseeId > 0 && skinId is > 0 and <= int.MaxValue => Read(
                    licenseeId,
                    (int)skinId
                ),
                ("ActivateSkinLicense", [List<object?> items]) => Activate(context, items),
                _ => new PilotCallResult.Failure("Choose valid pilot and skin information."),
            };
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private BlueList<ShipSkinLicenseReply> List(long pilotId, int? hullTypeId = null) =>
        new(licenses.List(pilotId, hullTypeId).Select(license => new ShipSkinLicenseReply(license)).ToArray());

    private PilotCallResult Read(long licenseeId, int skinId) =>
        licenses.Read(licenseeId, skinId) is { } license ? new ShipSkinLicenseReply(license) : PilotCallResult.None;

    private PilotCallResult Activate(PilotCallContext context, List<object?> items)
    {
        if (items.Count == 0)
        {
            return PilotCallResult.None;
        }
        long pilotId = context.Pilot.PilotId;
        foreach (var activation in licenses.Activate(pilotId, BlueRead.PositiveIds(items, "SKIN license item IDs")))
        {
            InventoryChangesWire.NotifyOwnedItemChange(context, activation.ItemChange);
            context.Notifications.Add(new ShipSkinLicenseActivated(activation.License.SkinId, pilotId));
        }
        return PilotCallResult.None;
    }
}
