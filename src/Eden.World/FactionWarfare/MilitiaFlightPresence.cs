using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Registration;

namespace Eden.World.FactionWarfare;

[Injectable(InjectionType.Singleton)]
internal sealed class MilitiaFlightPresence : IMilitiaFlightPresence
{
    private sealed record Presence(long ConnectionId, MilitiaComplexPilot Pilot);

    private readonly object _gate = new();
    private readonly Dictionary<int, Dictionary<long, Presence>> _systems = [];
    private long _generation;

    public void Publish(int systemId, MilitiaComplexPilot pilot)
    {
        if (!FactionWarzoneCatalog.Systems.ContainsKey(systemId))
        {
            return;
        }
        lock (_gate)
        {
            if (!_systems.TryGetValue(systemId, out var ships))
            {
                ships = [];
                _systems.Add(systemId, ships);
            }
            // A ship that becomes ineligible and returns between ticks starts a new observation interval.
            long generation =
                ships.GetValueOrDefault(pilot.ShipId) is { } prior && prior.ConnectionId == pilot.ConnectionId
                    ? prior.Pilot.ConnectionId
                    : checked(++_generation);
            ships[pilot.ShipId] = new(pilot.ConnectionId, pilot with { ConnectionId = generation });
        }
    }

    public void Withdraw(int systemId, long shipId)
    {
        if (!FactionWarzoneCatalog.Systems.ContainsKey(systemId))
        {
            return;
        }
        lock (_gate)
        {
            if (_systems.TryGetValue(systemId, out var ships) && ships.Remove(shipId) && ships.Count == 0)
            {
                _systems.Remove(systemId);
            }
        }
    }

    internal int[] Systems()
    {
        lock (_gate)
        {
            return _systems.Keys.ToArray();
        }
    }

    internal MilitiaComplexPilot[] Pilots(int systemId)
    {
        lock (_gate)
        {
            return _systems.TryGetValue(systemId, out var ships) ? ships.Values.Select(entry => entry.Pilot).ToArray() : [];
        }
    }
}
