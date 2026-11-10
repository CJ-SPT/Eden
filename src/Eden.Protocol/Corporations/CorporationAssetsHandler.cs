using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationAssetsHandler(ICorporationStorage storage) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "corpmgr" || method is not ("GetAssetInventory" or "GetAssetInventoryForLocation"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (
                keywords is { Count: > 0 }
                || args.Length == 0
                || args[0] is not long corporation
                || corporation != context.Pilot.CorpId
                || corporation is <= 0 or > int.MaxValue
            )
            {
                throw new InvalidDataException("Read assets for your current corporation.");
            }
            long actor = context.Pilot.PilotId;
            if (method == "GetAssetInventory" && args is [_, var selector] && BlueMarshal.Text(selector) == "offices")
            {
                storage.Assets(actor, (int)corporation, context.Pilot.NpcStationId);
                var offices = storage.Offices(actor);
                return new CorporationOfficeAssetsReply(
                    offices
                        .Select(o => new CorporationOfficeAssetRow(o, storage.Assets(actor, (int)corporation, o.StationId).Count))
                        .ToArray()
                );
            }
            if (
                method == "GetAssetInventoryForLocation"
                && args is [_, long station, var which]
                && station is > 0 and <= int.MaxValue
                && BlueMarshal.Text(which) == "offices"
            )
            {
                return new BlueList<InventoryRow>(
                    storage.Assets(actor, (int)corporation, (int)station).Select(InventoryRow.From).ToArray()
                );
            }
            throw new InvalidDataException("Only rented corporation office assets are supported.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CorporationOfficeAssetsReply(IReadOnlyList<CorporationOfficeAssetRow> Offices) : BlueModel
{
    public override object ToBlue() => new CompatibilityRowset(["locationID", "solarsystemID", "itemCount"], Offices).ToBlue();
}

internal sealed record CorporationOfficeAssetRow(CorporationOffice Office, int Count) : BlueModel
{
    public override object ToBlue() => new object?[] { Office.StationId, Office.SystemId, Count };
}
