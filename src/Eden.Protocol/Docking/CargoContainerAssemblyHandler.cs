using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class CargoContainerAssemblyHandler(ICargoContainerAssembly assembly) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) != ("invbroker", "AssembleCargoContainer"))
        {
            return PilotCallResult.Unsupported;
        }
        // Build 3662168 MenuSvc.AssembleContainer supplies one item per call with two default arguments.
        if (args is not [long itemId, null, 0d] || itemId <= 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Choose a packaged cargo container to assemble.");
        }
        try
        {
            foreach (var change in assembly.Assemble(context.Pilot.PilotId, itemId))
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
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
