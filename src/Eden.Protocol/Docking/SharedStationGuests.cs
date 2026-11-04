using Eden.Gameplay.Docking;
using Eden.Protocol.Flight;

namespace Eden.Protocol.Docking;

[Injectable(InjectionType.Singleton)]
internal sealed class SharedStationGuests
{
    private sealed record Presence(int StationId, PilotContact Pilot);

    private readonly object _sync = new();
    private readonly Dictionary<object, Presence> _connections = new();

    public void Update(object connection, DockedPilot? pilot)
    {
        lock (_sync)
        {
            if (pilot is { Position: null })
            {
                _connections[connection] = new(pilot.StationId, PilotContact.From(pilot));
            }
            else
            {
                _connections.Remove(connection);
            }
        }
    }

    public IReadOnlyList<PilotContact> List(DockedPilot pilot)
    {
        lock (_sync)
        {
            return pilot.Position is not null
                ? []
                : _connections
                    .Values.Where(entry => entry.StationId == pilot.StationId)
                    .Select(entry => entry.Pilot)
                    .DistinctBy(guest => guest.PilotId)
                    .OrderBy(guest => guest.PilotId)
                    .ToArray();
        }
    }
}
