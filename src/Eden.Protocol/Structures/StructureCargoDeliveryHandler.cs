using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class StructureCargoDeliveryHandler(IStructureCargoDelivery cargo) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) != ("structureCargoDelivery", "DropOffItems"))
        {
            return PilotCallResult.Unsupported;
        }
        // Build 3662168 DropBoxController.TransferItems sends list, active ship, then structure.
        if (
            keywords is { Count: > 0 }
            || args is not [List<object?> items, long ship, long structure]
            || ship != context.Pilot.ShipId
            || structure < StructureRules.FirstStructureId
        )
        {
            return new PilotCallResult.Failure("Choose cargo from your active ship and a valid Upwell structure.");
        }
        try
        {
            var ids = BlueRead.PositiveIds(items, "item IDs");
            var flight = context.Flight;
            flight.Checkpoint();
            if (!flight.CanUseSelfModules || flight.InWarp || flight.PreparingWarp || flight.Cloaked)
            {
                return new PilotCallResult.Failure("Drop off cargo from your active ship in space outside warp and cloak.");
            }
            var changes = cargo.DropOff(context.Pilot.PilotId, ship, structure, ids);
            foreach (var change in changes)
            {
                InventoryChangesWire.NotifyOwnedItemChange(context, change);
            }
            return new BlueList<long>(changes.Select(change => change.Item.Id).ToArray());
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
