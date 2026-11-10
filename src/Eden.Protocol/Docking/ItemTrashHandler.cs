using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class ItemTrashHandler(IPilotItemTrash trash) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "invbroker" || method != "TrashItems")
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            // Build 3662168 invItemFunctions.TrashInvItems supplies a list and station ID.
            // The native caller owns confirmation and accepts a falsy success reply.
            if (
                keywords is { Count: > 0 }
                || args is not [List<object?> ids, long station]
                || station <= 0
                || ids.Any(id => id is not long)
            )
            {
                throw new InvalidDataException("Select personal station items to trash.");
            }
            foreach (var change in trash.Trash(context.Pilot.PilotId, station, ids.Cast<long>().ToArray()))
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
