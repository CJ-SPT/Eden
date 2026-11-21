using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class FleetHandler(
    IPilotFleet fleet,
    ShipDogmaBuilder dogma,
    DeferredRefitPrimes refits,
    PilotBrain brain,
    ICargoContainers containers,
    IPilotShipSharing? sharing = null,
    IPilotShipMaintenance? maintenance = null
) : ISelectedPilotCallHandler
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
            return RenameItem(context, args, keywords);
        }
        if ((service, method) == ("ship", "GetShipConfiguration"))
        {
            if (args is not [long id] || keywords is { Count: > 0 })
            {
                return new PilotCallResult.Failure("Choose a ship to read its sharing configuration.");
            }
            var settings = sharing?.Configuration(context.Pilot.PilotId, id);
            return id == (context.ControlledStructureId ?? context.Pilot.ShipId) || settings is not null
                ? new ShipSharingConfigurationWire(settings)
                : new PilotCallResult.Failure("The ship sharing configuration is unavailable at your current location.");
        }
        if ((service, method) == ("ship", "ConfigureShip"))
        {
            try
            {
                if (
                    sharing is null
                    || context.ControlledStructureId is not null
                    || args is not [Dictionary<string, object?> fields]
                    || fields.Count == 0
                    || keywords is { Count: > 0 }
                    || fields.Any(field =>
                        field.Value is not bool && field.Value is not (0L or 1L)
                        || field.Key
                            is not (
                                ShipSharingConfigurationWire.FleetHangarFleet
                                or ShipSharingConfigurationWire.FleetHangarCorporation
                                or ShipSharingConfigurationWire.MaintenanceFleet
                                or ShipSharingConfigurationWire.MaintenanceCorporation
                            )
                    )
                )
                {
                    throw new InvalidDataException("Choose fleet or corporation access for a shared bay on your active ship.");
                }
                sharing.Configure(
                    context.Pilot.PilotId,
                    context.Pilot.ShipId,
                    new(
                        Setting(ShipSharingConfigurationWire.FleetHangarFleet),
                        Setting(ShipSharingConfigurationWire.FleetHangarCorporation),
                        Setting(ShipSharingConfigurationWire.MaintenanceFleet),
                        Setting(ShipSharingConfigurationWire.MaintenanceCorporation)
                    )
                );
                return PilotCallResult.None;

                bool? Setting(string name) =>
                    fields.GetValueOrDefault(name) switch
                    {
                        bool value => value,
                        long value => value != 0,
                        _ => null,
                    };
            }
            catch (InvalidDataException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        if (service == "ship" && method is "StoreVessel" or "BoardStoredShip")
        {
            return UseMaintenanceBay(context, method, args, keywords);
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

    private PilotCallResult UseMaintenanceBay(
        PilotCallContext context,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords
    )
    {
        if (maintenance is null || keywords is { Count: > 0 } || context.ControlledStructureId is not null)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            InventoryTransfers.CheckRefitFlight(context);
            var pilot = context.Pilot;
            var changed = (method, args) switch
            {
                ("StoreVessel", [long host]) => maintenance.Store(pilot.PilotId, pilot.ShipId, host),
                ("BoardStoredShip", [long host, long stored]) => maintenance.Board(pilot.PilotId, pilot.ShipId, host, stored),
                _ => throw new InvalidDataException("Choose an available ship maintenance bay and stored ship."),
            };
            refits.Clear();
            context.Flight.RetireStoredShip();
            context.ChangeShip(changed.Pilot);
            // The system stays the same, so send the new ego's ballpark state with the ship change.
            context.Flight.Snapshot();
            RefreshBrain(context);
            foreach (var change in changed.Changes)
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            return method == "StoreVessel" ? (PilotCallResult)changed.Pilot.ShipId : dogma.ShipState(changed.Pilot);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private PilotCallResult RenameItem(PilotCallContext context, object?[] args, Dictionary<string, object?>? keywords)
    {
        try
        {
            if (args is not [long id, string or byte[]] || keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Choose a ship or container and a valid name.");
            }
            string name = containers.Handles(id)
                ? containers.Rename(context.Pilot.PilotId, id, BlueMarshal.Text(args[1]))
                : fleet.RenameDockedShip(context.Pilot.PilotId, id, BlueMarshal.Text(args[1]));
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
