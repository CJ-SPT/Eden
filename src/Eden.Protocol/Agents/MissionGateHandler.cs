using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Fleets;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Agents;

[Injectable]
internal sealed class MissionGateHandler(CooperativeFleetHandler fleets) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "keeper" || method != "ActivateAccelerationGate")
        {
            return PilotCallResult.Unsupported;
        }
        // Build 3662168 movementFunctions.RealActivateAccelerationGate passes one item ID.
        // violateSafetyTimer belongs to sessionMgr.PerformSessionChange, not this remote call.
        if (args is not [long gateId] || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Invalid acceleration gate request.");
        }
        if (context.Pilot.Position is null)
        {
            return new PilotCallResult.Failure("Undock before activating an acceleration gate.");
        }
        fleets.CancelWarpOrders(context.Pilot.PilotId);
        var result = context.Flight.Execute(new FlightCommand.ActivateAccelerationGate(gateId));
        foreach (var item in context.Flight.TakeGateInventoryChanges())
        {
            InventoryChangesWire.NotifyItemChange(context, item, beforeFlight: true);
        }
        return result;
    }
}
