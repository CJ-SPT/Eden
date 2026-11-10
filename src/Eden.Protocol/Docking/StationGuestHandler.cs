using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable(InjectionType.Scoped)]
internal sealed class StationGuestHandler(SharedStationGuests guests) : ISelectedPilotCallHandler, IDisposable
{
    private long? _stationId;
    private StationGuest[] _observed = [];

    public bool IsDocked => _stationId.HasValue;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool structure = service == "structureGuests" && method == "GetGuests";
        if (!structure && (service != "station" || method != "GetGuests"))
        {
            return PilotCallResult.Unsupported;
        }
        if (
            (
                structure
                    ? args is not [long location] || !context.Pilot.InStructure || location != context.Pilot.DockedLocationId
                    : args.Length != 0
            ) || keywords is { Count: > 0 }
        )
        {
            return new PilotCallResult.Failure("Station guests does not accept arguments.");
        }
        // The client caches this snapshot until its station changes. Establish
        // the notification baseline from the same snapshot returned by the RPC.
        _observed = Read(context);
        return structure ? new StructureGuestsReply(_observed) : new BlueList<StationGuest>(_observed);
    }

    public void Advance(PilotCallContext context)
    {
        long? previousStation = _stationId;
        var current = Read(context);
        if (_stationId == previousStation && _stationId.HasValue)
        {
            foreach (var departed in _observed.Except(current))
            {
                context.Notifications.Add(
                    context.Pilot.InStructure ? new StructureGuestDeparted(departed) : new StationGuestDeparted(departed)
                );
            }
            foreach (var arrived in current.Except(_observed))
            {
                context.Notifications.Add(
                    context.Pilot.InStructure ? new StructureGuestArrived(arrived) : new StationGuestArrived(arrived)
                );
            }
        }
        _observed = current;
    }

    private StationGuest[] Read(PilotCallContext context)
    {
        var pilot = context.ActiveFlight?.Pilot;
        guests.Update(this, pilot);
        _stationId = pilot is { Position: null } ? pilot.DockedLocationId : null;
        return pilot is null ? [] : guests.List(pilot).Select(guest => new StationGuest(guest.PilotId, guest.CorpId)).ToArray();
    }

    public void Dispose()
    {
        guests.Update(this, null);
        _stationId = null;
        _observed = [];
    }
}
