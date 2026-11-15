using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class GlobalInventoryHandler(IPilotAssets assets) : ISelectedPilotCallHandler
{
    // Build 3662168 inventorycommon.const.containerGlobal and invCache.GetInventory.
    internal const long ContainerId = 10002;
    internal const string BoundService = "@globalInventory";

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != BoundService
            || method is not ("ListStations" or "ListStationItems" or "List" or "ListIncludingContainers" or "GetAssetWorth")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected personal asset arguments.");
            }
            long pilot = context.Pilot.PilotId;
            return (method, args) switch
            {
                ("ListStations", []) => new GlobalInventoryStations(assets.Locations(pilot)),
                ("List", []) => Rows(assets.Items(pilot, new())),
                ("ListIncludingContainers", []) => Rows(assets.Items(pilot, new(IncludeContainers: true))),
                ("ListStationItems", [long station]) when station > 0 => Rows(assets.Items(pilot, new(station))),
                ("GetAssetWorth", []) => new GlobalInventoryWorth(assets.Worth(pilot)),
                _ => new PilotCallResult.Failure("Invalid personal asset request."),
            };
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static BlueList<InventoryRow> Rows(IReadOnlyList<InventoryItem> items) => new(items.Select(InventoryRow.From).ToArray());
}

internal sealed record GlobalInventoryWorth(PersonalAssetWorth Worth) : BlueModel
{
    public override object ToBlue() => new object?[] { (double)Worth.AssetsIsk, (double)Worth.PlexIsk };
}

internal sealed record GlobalInventoryStations(IReadOnlyList<PersonalAssetLocation> Stations) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(
            ["stationID", "itemCount", "solarSystemID", "typeID"],
            Stations.Select(s => new GlobalInventoryStationRow(s)).ToArray()
        ).ToBlue();
}

internal sealed record GlobalInventoryStationRow(PersonalAssetLocation Station) : BlueModel
{
    public override object ToBlue() => new object?[] { Station.StationId, Station.ItemCount, Station.SolarSystemId, Station.TypeId };
}
