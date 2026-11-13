using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class FleetHandler(IPilotFleet fleet, ShipDogmaBuilder dogma, DeferredRefitPrimes refits, PilotBrain brain)
    : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) == ("invbroker", "SetLabel"))
        {
            return RenameShip(context, args, keywords);
        }
        if ((service, method) == ("ship", "GetShipConfiguration"))
        {
            return args is [long id] && id == context.Pilot.ShipId
                ? new ShipSharingConfiguration()
                : new PilotCallResult.Failure("Read the configuration of your active ship.");
        }
        if ((service, method) == ("ship", "ConfigureShip"))
        {
            return new PilotCallResult.Failure("Fleet and corporation access to personal ship holds is not available yet.");
        }
        if (
            !(service == "ship" && method is "AssembleShip" or "Board" or "LeaveShip")
            && !(service == "dogmaIM" && method == "CreateNewbieShip")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (method == "LeaveShip" && args is [long leavingShip] && keywords is not { Count: > 0 })
            {
                var leaving = fleet.Leave(context.Pilot.PilotId, leavingShip);
                refits.Clear();
                context.ChangeShip(leaving.Pilot);
                RefreshBrain(context);
                foreach (var change in leaving.Changes)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                return PilotCallResult.None;
            }
            if (service == "dogmaIM" && args is [long previousShip, long station])
            {
                var recovery = fleet.RecoverCorvette(context.Pilot.PilotId, previousShip, station);
                refits.Clear();
                context.ChangeShip(recovery.Pilot);
                RefreshBrain(context);
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
                var boarding = fleet.Board(context.Pilot.PilotId, shipId, oldShipId);
                refits.Clear();
                context.ChangeShip(boarding.Pilot);
                RefreshBrain(context);
                foreach (var change in boarding.Changes)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                return dogma.ShipState(boarding.Pilot);
            }
            return new PilotCallResult.Failure("Unsupported ship assembly or boarding request.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private PilotCallResult RenameShip(PilotCallContext context, object?[] args, Dictionary<string, object?>? keywords)
    {
        try
        {
            if (args is not [long id, string or byte[]] || keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Choose a ship and a valid name.");
            }
            string name = fleet.RenameDockedShip(context.Pilot.PilotId, id, BlueMarshal.Text(args[1]));
            if (id == context.Pilot.ShipId)
            {
                context.Flight.RenameShip(name);
            }
            context.Notifications.Add(new ShipLabelChanged(id, name));
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private void RefreshBrain(PilotCallContext context)
    {
        // GetDogmaLocation(charBrain=...) reuses the existing client location on
        // boarding. Explicitly replace its old hull's skill selectors as well.
        context.Notifications.Add(new ServerBrainUpdated(brain.Update(context.Pilot.PilotId, context.Pilot.ShipId)));
    }
}
