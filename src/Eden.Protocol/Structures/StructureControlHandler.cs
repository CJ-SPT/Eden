using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable(InjectionType.Scoped)]
internal sealed class StructureControlHandler(
    IPilotStructures structures,
    IStructureContacts contacts,
    SharedStructureControl controllers,
    StructureDogmaBuilder dogma,
    FittingOperations fitting,
    Eden.Gameplay.Docking.IPilotLookup pilots
) : ISelectedPilotCallHandler
{
    private sealed record HangarState(long StructureId, StructureContact Contact);

    private HangarState? _hangarState;

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
            if (service == "structureDocking" && method == "Undock")
            {
                if (
                    args is not [long structureId, var requestedShip]
                    || !context.Pilot.InStructure
                    || context.Pilot.DockedLocationId != structureId
                    || !(
                        Equals(requestedShip, context.Pilot.ShipId) || requestedShip is null && context.ControlledStructureId == structureId
                    )
                    || keywords?.Keys.Any(key => key != "ignoreContraband") == true
                    || keywords?.GetValueOrDefault("ignoreContraband") is not (null or false or true or 0L or 1L)
                )
                {
                    throw new InvalidDataException("Undock from your current structure in your active ship.");
                }
                var result = context.Flight.Execute(new FlightCommand.Undock(context.Pilot.ShipId));
                if (result is PilotCallResult.Success)
                {
                    context.ReleaseStructureControl();
                    context.Flight.Updates.Clear();
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
                    context.TakeStructureControl(controllers.Acquire(structureId, pilot, context.SessionId));
                    context.Flight.RefreshStructures(contacts.Contacts(context.Pilot.SystemId));
                    context.Flight.Snapshot();
                    return PilotCallResult.None;
                }
                if (method == "ReleaseControl" && args.Length == 0)
                {
                    context.ReleaseStructureControl();
                    context.Flight.Snapshot();
                    return PilotCallResult.None;
                }
                if (method == "EjectFromStructure" && args is [var restore] && (restore is null || Equals(restore, context.Pilot.ShipId)))
                {
                    context.ReleaseStructureControl();
                    context.Flight.Snapshot();
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
            if (method == "GetAllInfo" && !DogmaFittingBrain.IsRequested(pilot, args, keywords))
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
                method == "ItemGetInfo"
                && context.ControlledStructureId == id
                && args is [object?[] { Length: 3 } key]
                && key is [long source, long slot, long type]
                && source == id
            )
            {
                var magazine = structures.Read(pilot, id)?.Magazines.FirstOrDefault(item => item.FlagId == slot && item.TypeId == type);
                return magazine is null ? PilotCallResult.None : dogma.PrimeCharge(context.Pilot, magazine);
            }
            if (method == "ItemGetInfo" && args is [long moduleId] && context.ControlledStructureId == id)
            {
                var structure = structures.Read(pilot, id)!;
                var fittedItem = structures.Inventory(pilot, id).SingleOrDefault(i => i.Id == moduleId);
                if (fittedItem is not null && structure.Modules.Concat(structure.Equipment).Any(m => m.ItemId == moduleId))
                {
                    return dogma.PrimeModule(context.Pilot, structure, fittedItem);
                }
            }
            if (
                context.ControlledStructureId == id
                && args is [long ship, long module]
                && ship == id
                && method is "SetModuleOnline" or "TakeModuleOffline"
            )
            {
                return fitting.SetOnline(context, id, module, method == "SetModuleOnline");
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
        if (!context.Selected)
        {
            _hangarState = null;
            return;
        }
        if (context.Pilot.InStructure)
        {
            long docked = context.Pilot.DockedLocationId;
            var contact = contacts.Contacts(context.Pilot.SystemId).FirstOrDefault(body => body.Id == docked)?.Structure;
            if (contact is null || contact.State == StructureState.Unanchored)
            {
                var evacuated = pilots.ListPilots(context.AccountId).Single(p => p.PilotId == context.Pilot.PilotId);
                if (evacuated.Position is not null)
                {
                    context.ChangeShip(evacuated);
                    context.Flight.Snapshot();
                    _hangarState = null;
                }
            }
            else if (_hangarState != new HangarState(docked, contact))
            {
                if (structures.Read(context.Pilot.PilotId, docked) is { } structure)
                {
                    context.Notifications.Add(new StructureHangarChanged(structure));
                    context.Notifications.Add(new StructureServicesChanged(docked));
                }
                _hangarState = new(docked, contact);
            }
        }
        else
        {
            _hangarState = null;
        }
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
