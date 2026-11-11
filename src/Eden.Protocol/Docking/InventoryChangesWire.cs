using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

internal static class InventoryChangesWire
{
    internal static void NotifyItemChange(PilotCallContext context, InventoryChange change, bool beforeFlight = false)
    {
        // OnItemChange keys index the inventory DBRow; values describe the old row.
        // Build 3662168 removes exhausted stacks through a junk-location change.
        // Keep the detached stack's last quantity: the UI reconstructs the old
        // row's quantity but reads its separate stacksize when deciding to remove
        // a tile. A zero stacksize makes both the old and new rows appear absent.
        var item =
            change.Item.Quantity == 0
                ? change.Item with
                {
                    LocationId = (int)InventoryLocation.Graveyard,
                    FlagId = (int)InventoryFlag.AutoFit,
                    Online = false,
                    Quantity = change.Previous?.Quantity ?? 0,
                }
                : change.Item;
        Dictionary<InventoryColumn, long> old = [];
        if (change.Previous is not { } previous)
        {
            // A new stack must enter the cached container. A quantity-only change
            // updates capacity but does not insert an unknown item in invCache.
            old[InventoryColumn.Location] = 0L;
            old[InventoryColumn.Flag] = 0;
            // Assembled rows encode quantity as -1. invCache rejects a quantity
            // change on that row; stacksize still describes its prior absence.
            if (!item.Singleton)
            {
                old[InventoryColumn.Quantity] = 0;
            }
            old[InventoryColumn.StackSize] = 0;
        }
        else
        {
            if (previous.TypeId != item.TypeId)
            {
                old[InventoryColumn.Type] = previous.TypeId;
            }
            if (previous.GroupId != item.GroupId)
            {
                old[InventoryColumn.Group] = previous.GroupId;
            }
            if (previous.CategoryId != item.CategoryId)
            {
                old[InventoryColumn.Category] = previous.CategoryId;
            }
            if (previous.OwnerId != item.OwnerId)
            {
                old[InventoryColumn.Owner] = previous.OwnerId;
            }
            if (previous.LocationId != item.LocationId)
            {
                old[InventoryColumn.Location] = previous.LocationId;
            }
            if (previous.FlagId != item.FlagId)
            {
                old[InventoryColumn.Flag] = previous.FlagId;
            }
            if (previous.Singleton != item.Singleton)
            {
                old[InventoryColumn.Singleton] = previous.Singleton ? 1 : 0;
            }
            if (previous.Quantity != item.Quantity)
            {
                old[InventoryColumn.Quantity] = previous.Quantity;
                old[InventoryColumn.StackSize] = previous.Quantity;
            }
        }
        var notification = new ItemChanged(InventoryRow.From(item), old);
        if (beforeFlight)
        {
            context.NotificationsBeforeFlight.Add(notification);
        }
        else
        {
            context.Notifications.Add(notification);
        }
        if (item.CategoryId == (int)InventoryCategory.Blueprint)
        {
            NotifyBlueprintsUpdated(context, item.OwnerId);
        }
    }

    internal static void NotifyBlueprintsUpdated(PilotCallContext context, long ownerId)
    {
        // BlueprintSvc memoizes both the owner's list and facility summaries
        // independently of invCache, including an empty list before a purchase.
        if (!context.Notifications.Any(n => n is BlueprintsUpdated update && update.OwnerId == ownerId))
        {
            context.Notifications.Add(new BlueprintsUpdated(ownerId));
        }
    }
}
