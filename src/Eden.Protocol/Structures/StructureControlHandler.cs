using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class StructureControlHandler(
    IPilotStructures structures,
    IStructureContacts contacts,
    SharedStructureControl controllers,
    StructureDogmaBuilder dogma,
    TimeProvider clock
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
        try
        {
            long pilot = context.Pilot.PilotId;
            if (service == "structureDocking" && method == "Undock" && context.ControlledStructureId is long controlled)
            {
                if (
                    args is not [long structureId, var requestedShip]
                    || structureId != controlled
                    || !context.Pilot.InStructure
                    || context.Pilot.DockedLocationId != controlled
                    || requestedShip is not null && !Equals(requestedShip, context.Pilot.ShipId)
                    || keywords?.Keys.Any(key => key != "ignoreContraband") == true
                    || keywords?.GetValueOrDefault("ignoreContraband") is not (null or false or true or 0L or 1L)
                )
                {
                    throw new InvalidDataException("Undock from your controlled structure in your active ship.");
                }
                var result = context.Flight.Execute(new FlightCommand.Undock(context.Pilot.ShipId));
                if (result is PilotCallResult.Success)
                {
                    context.ReleaseStructureControl();
                    context.Flight.Snapshot();
                }
                return result;
            }
            if (service == "structureControl")
            {
                if (keywords is { Count: > 0 })
                {
                    throw new InvalidDataException("Unexpected structure control arguments.");
                }
                if (method == "GetStructurePilot" && args is [long requested] && requested > 0)
                {
                    return controllers.Pilot(requested) is long controller ? controller : PilotCallResult.None;
                }
                if (method == "TakeControl" && args is [long structureId] && structureId >= StructureRules.FirstStructureId)
                {
                    _ = structures.AuthorizeControl(pilot, structureId);
                    context.Flight.RefreshStructures(contacts.Contacts(context.Pilot.SystemId));
                    context.TakeStructureControl(controllers.Acquire(structureId, pilot, context.SessionId));
                    context.Flight.Snapshot();
                    return PilotCallResult.None;
                }
                if (method == "ReleaseControl" && args.Length == 0)
                {
                    context.ReleaseStructureControl();
                    return PilotCallResult.None;
                }
                if (method == "EjectFromStructure" && args is [var restore] && (restore is null || Equals(restore, context.Pilot.ShipId)))
                {
                    context.ReleaseStructureControl();
                    return PilotCallResult.None;
                }
                if (method == "BoardStructure")
                {
                    throw new InvalidDataException("Dock in the structure before taking control.");
                }
                throw new InvalidDataException("Choose a valid structure control operation.");
            }
            if (service != "dogmaIM" || !context.Pilot.InStructure)
            {
                return PilotCallResult.Unsupported;
            }
            long id = context.Pilot.DockedLocationId;
            if (method == "GetAllInfo")
            {
                return dogma.AllInfo(
                    context.Pilot,
                    structures.Read(pilot, id) ?? throw new InvalidDataException("The docked structure is unavailable."),
                    context.ControlledStructureId == id
                );
            }
            if (method == "ItemGetInfo" && args is [long item] && item == id)
            {
                return structures.Read(pilot, id) is { } structure ? dogma.Prime(structure) : PilotCallResult.None;
            }
            if (
                context.ControlledStructureId == id
                && args is [long ship, long module]
                && ship == id
                && method is "SetModuleOnline" or "TakeModuleOffline"
            )
            {
                var result = structures.SetServiceOnline(pilot, id, module, method == "SetModuleOnline");
                StructureHandler.Notify(context, result);
                bool online = method == "SetModuleOnline";
                long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
                context.Notifications.Add(
                    new Eden.Protocol.Docking.ShipEffectChanged(
                        new(module, pilot, id, null, Eden.Gameplay.Inventory.DogmaEffect.Online, now, null, 0),
                        now,
                        online,
                        online
                    )
                );
                context.Flight.RefreshStructures(contacts.Contacts(context.Pilot.SystemId));
                return PilotCallResult.None;
            }
            return PilotCallResult.Unsupported;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    internal void Advance(PilotCallContext context)
    {
        if (context.ControlledStructureId is not long structureId)
        {
            return;
        }
        if (
            !context.Pilot.InStructure
            || context.Pilot.DockedLocationId != structureId
            || !structures.CanControl(context.Pilot.PilotId, structureId)
        )
        {
            context.ReleaseStructureControl();
            return;
        }
        context.Flight.RefreshStructures(contacts.Contacts(context.Pilot.SystemId));
    }
}
