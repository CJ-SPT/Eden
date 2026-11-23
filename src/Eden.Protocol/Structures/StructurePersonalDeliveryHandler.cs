using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class StructurePersonalDeliveryHandler(IStructurePersonalDelivery delivery) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) != ("structureDeliveries", "Deliver"))
        {
            return PilotCallResult.Unsupported;
        }
        var pilot = context.Pilot;
        if (
            keywords is { Count: > 0 }
            || args is not [long structure, long recipient, List<object?> items]
            || !pilot.InStructure
            || structure != pilot.DockedLocationId
            || recipient <= 0
        )
        {
            return new PilotCallResult.Failure("Choose a recipient and items to deliver in your current structure.");
        }
        try
        {
            var changes = delivery.Deliver(pilot.PilotId, recipient, structure, BlueRead.PositiveIds(items, "item IDs"));
            foreach (var change in changes)
            {
                if (change.Item.OwnerId == pilot.PilotId)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                else if (change.Previous is { } previous)
                {
                    InventoryChangesWire.NotifyItemChange(context, new(previous with { Quantity = 0 }, previous));
                }
            }
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
