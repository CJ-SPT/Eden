using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class FleetHandler(IPilotFleet fleet, InventoryHandler inventories) : ISelectedPilotCallHandler
{
    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        if (service != "ship" || method is not ("AssembleShip" or "Board")) { return PilotCallRouter.Unsupported; }
        try
        {
            if (method == "AssembleShip" && args is [List<object?> items] && items.All(i => i is long) &&
                keywords?.GetValueOrDefault("subSystems") is null)
            {
                object? nameValue = keywords?.GetValueOrDefault("name");
                string? name = nameValue is null ? null : nameValue is string or byte[]? BlueMarshal.Text(nameValue) :
                    throw new InvalidDataException("Invalid ship name.");
                var result = fleet.Assemble(context.Pilot.PilotId, items.Cast<long>().ToArray(), name);
                foreach (var change in result.Changes) { InventoryHandler.NotifyItemChange(context, change); }
                return result.ShipIds.Cast<object?>().ToList();
            }
            if (method == "Board" && args is [long shipId, long oldShipId])
            {
                var pilot = fleet.Board(context.Pilot.PilotId, shipId, oldShipId);
                context.ChangeShip(pilot);
                return ((Dictionary<string, object?>)inventories.AllInfo(pilot).State!)["shipState"];
            }
            return new PilotCallRouter.Error("Unsupported ship assembly or boarding request.");
        }
        catch (InvalidDataException error) { return new PilotCallRouter.Error(error.Message); }
    }
}
