using Eden.Gameplay.Contracts;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class GlobalInventoryHandler(IPilotContractAssets assets) : ISelectedPilotCallHandler
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
        if (service != BoundService || method != "ListStations")
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Invalid personal asset station request.");
        }
        return new GlobalInventoryStations(assets.Stations(context.Pilot.PilotId));
    }
}

internal sealed record GlobalInventoryStations(IReadOnlyList<ContractAssetStation> Stations) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(["stationID", "itemCount"], Stations.Select(s => new GlobalInventoryStationRow(s)).ToArray()).ToBlue();
}

internal sealed record GlobalInventoryStationRow(ContractAssetStation Station) : BlueModel
{
    public override object ToBlue() => new object?[] { Station.StationId, Station.ItemCount };
}
