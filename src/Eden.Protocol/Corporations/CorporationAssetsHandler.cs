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
        if (service != "corpmgr" || method is not ("GetAssetInventory" or "GetAssetInventoryForLocation" or "SearchAssets"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation asset arguments.");
            }
            long actor = context.Pilot.PilotId;
            if (method == "SearchAssets")
            {
                if (
                    args is not [var searchSelector, var category, var group, var type, null or long and >= 0]
                    || BlueRead.Text(searchSelector) != "offices"
                )
                {
                    throw new InvalidDataException("Choose office assets and valid search filters.");
                }
                var search = new CorporationOfficeAssetSearch(
                    OptionalInt(category),
                    OptionalInt(group),
                    OptionalInt(type),
                    args[4] as long? ?? 0
                );
                return new CorporationOfficeAssetSearchReply(storage.SearchOfficeAssets(actor, search));
            }
            if (
                args.Length == 0
                || args[0] is not long corporation
                || corporation != context.Pilot.CorpId
                || corporation is <= 0 or > int.MaxValue
            )
            {
                throw new InvalidDataException("Read assets for your current corporation.");
            }
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
                return new CorporationOfficeAssetDetailsReply(
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

    private static int OptionalInt(object? value) =>
        value switch
        {
            null => 0,
            long id when id is >= 0 and <= int.MaxValue => (int)id,
            _ => throw new InvalidDataException("Choose a valid asset category, group or type."),
        };
}

internal sealed record CorporationOfficeAssetSearchReply(IReadOnlyList<int> Stations) : BlueModel
{
    private static readonly BlueConstructor s_header = BlueRows.Descriptor([("locationID", BlueColumnType.Int64)]);

    public override object ToBlue() =>
        new BlueConstructor(
            "carbon.common.script.sys.crowset.CRowset",
            [
                s_header,
                Stations
                    .Select(station => (object?)new BlueConstructor("blue.DBRow", [s_header, new object?[] { (long)station }]))
                    .ToList(),
            ]
        );
}

internal sealed record CorporationOfficeAssetsReply(IReadOnlyList<CorporationOfficeAssetRow> Offices) : BlueModel
{
    public override object ToBlue() => new CompatibilityRowset(["locationID", "solarsystemID", "itemCount"], Offices).ToBlue();
}

internal sealed record CorporationOfficeAssetDetailsReply(IReadOnlyList<InventoryRow> Items) : BlueModel
{
    // Build 3662168 CorpAccounts.GetSubContent extends the rowset header's virtual columns.
    public override object ToBlue() =>
        new BlueConstructor("carbon.common.script.sys.crowset.CRowset", [InventoryWire.Descriptor(), Items.Cast<object?>().ToList()]);
}

internal sealed record CorporationOfficeAssetRow(CorporationOffice Office, int Count) : BlueModel
{
    public override object ToBlue() => new object?[] { Office.StationId, Office.SystemId, Count };
}
