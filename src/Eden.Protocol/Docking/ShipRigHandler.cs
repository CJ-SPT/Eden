using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class ShipRigHandler(IPilotRigRemoval rigs, FittingOperations fitting) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (method != "DestroyFitting" || (service != "@ship" && service != $"@ship:{context.Pilot.ShipId}"))
        {
            return PilotCallResult.Unsupported;
        }
        if (args is not [long itemId] || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Choose one fitted ship rig to destroy.");
        }
        try
        {
            var banks = fitting.ReadBanks(context);
            var changes = rigs.DestroyRig(context.Pilot.PilotId, context.Pilot.ShipId, itemId);
            foreach (var change in changes)
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            fitting.NotifyOnlineChanges(context, changes);
            fitting.NotifyShipAttributes(context, banks);
            return PilotCallResult.None;
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
