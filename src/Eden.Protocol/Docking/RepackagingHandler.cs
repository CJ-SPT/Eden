using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class RepackagingHandler(IPilotRepackaging repackaging) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "repackagingSvc" || method != "RepackageItems")
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            // Build 3662168 groups (itemID, locationID) tuples by station ID.
            // Destructive rig/container confirmation forms remain unsupported.
            if (
                keywords is { Count: > 0 }
                || args is not [Dictionary<long, object?> stations]
                || stations.Count != 1
                || !stations.TryGetValue((long)context.Pilot.StationId, out var values)
                || values is not List<object?> entries
            )
            {
                throw new InvalidDataException("Select ordinary items in your current station for repackaging.");
            }
            var items = entries
                .Select(value =>
                    value is object?[] { Length: 2 } pair && pair[0] is long id && pair[1] is long location
                        ? new RepackageItem(id, location)
                        : throw new InvalidDataException("Invalid repackaging item.")
                )
                .ToArray();
            foreach (var change in repackaging.Repackage(context.Pilot.PilotId, context.Pilot.StationId, items))
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
