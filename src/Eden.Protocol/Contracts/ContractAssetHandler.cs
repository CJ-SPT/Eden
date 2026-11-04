using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Contracts;

[Injectable]
internal sealed class ContractAssetHandler(IPilotContractAssets assets) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "contractProxy"
            || method is not ("GetItemsInDockableLocation" or "GetItemsInContainer" or "GetNumItemsInContainers" or "SplitStack")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected contract asset arguments.");
            }
            long pilot = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetItemsInDockableLocation", [long station, var corporation]):
                    Personal(corporation);
                    return Rows(assets.Station(pilot, Station(station)));
                case ("GetItemsInContainer", [long station, long container, var corporation, var flag]):
                    Personal(corporation);
                    Hangar(flag);
                    return Rows(assets.Contents(pilot, Station(station), container));
                case ("GetNumItemsInContainers", [long station, List<object?> items, var corporation, var flag]):
                    Personal(corporation);
                    Hangar(flag);
                    if (items.Count > ContractAssetRules.MaximumSelection || items.Any(i => i is not long))
                    {
                        throw new InvalidDataException("Invalid contract asset selection.");
                    }
                    return new ContractContainerCounts(assets.Counts(pilot, Station(station), items.Cast<long>().ToArray()));
                case ("SplitStack", [long station, long item, long quantity, var corporation, var flag])
                    when quantity is > 0 and <= int.MaxValue:
                    Personal(corporation);
                    Hangar(flag);
                    var changes = assets.Split(pilot, Station(station), item, (int)quantity);
                    foreach (var change in changes)
                    {
                        InventoryChangesWire.NotifyItemChange(context, change);
                    }
                    return true;
                default:
                    return new PilotCallResult.Failure("Invalid contract asset request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static int Station(long value) =>
        value is > 0 and <= int.MaxValue ? (int)value : throw new InvalidDataException("Choose a valid contract station.");

    private static void Personal(object? corporation)
    {
        if (corporation is not (false or 0L))
        {
            throw new InvalidDataException("Corporation contract assets are not available yet.");
        }
    }

    private static void Hangar(object? flag)
    {
        if (flag is not ((long)InventoryFlag.Hangar))
        {
            throw new InvalidDataException("Choose the personal station hangar.");
        }
    }

    private static BlueList<InventoryRow> Rows(IReadOnlyList<InventoryItem> items) => new(items.Select(InventoryRow.From).ToArray());
}

internal sealed record ContractContainerCounts(IReadOnlyList<ContractContainerCount> Counts) : BlueModel
{
    public override object ToBlue() => Counts.ToDictionary(c => c.ItemId, c => (object?)c.Count);
}
