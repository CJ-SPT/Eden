using Eden.Gameplay.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class FleetHandler(IPilotFleet fleet, InventoryHandler inventories) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (!(service == "ship" && method is "AssembleShip" or "Board") && !(service == "dogmaIM" && method == "CreateNewbieShip"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (service == "dogmaIM" && args is [long previousShip, long station])
            {
                var recovery = fleet.RecoverCorvette(context.Pilot.PilotId, previousShip, station);
                context.ChangeShip(recovery.Pilot);
                foreach (var change in recovery.Changes)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                return PilotCallResult.None;
            }
            if (
                method == "AssembleShip"
                && args is [List<object?> items]
                && items.All(i => i is long)
                && keywords?.GetValueOrDefault("subSystems") is null
            )
            {
                object? nameValue = keywords?.GetValueOrDefault("name");
                string? name =
                    nameValue is null ? null
                    : nameValue is string or byte[] ? BlueMarshal.Text(nameValue)
                    : throw new InvalidDataException("Invalid ship name.");
                var result = fleet.Assemble(context.Pilot.PilotId, items.Cast<long>().ToArray(), name);
                foreach (var change in result.Changes)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                return new BlueList<long>(result.ShipIds);
            }
            if (method == "Board" && args is [long shipId, long oldShipId])
            {
                var pilot = fleet.Board(context.Pilot.PilotId, shipId, oldShipId);
                context.ChangeShip(pilot);
                return inventories.AllInfo(pilot).ShipState;
            }
            return new PilotCallResult.Failure("Unsupported ship assembly or boarding request.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
