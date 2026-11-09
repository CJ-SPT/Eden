namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private void IndexJourney(long pilot, CapsuleerHauler hauler)
    {
        if (hauler.HasJourney)
        {
            _travelers.Add(pilot);
        }
        IndexRoute(pilot, hauler.RouteSystems);
    }

    private void IndexRoute(long pilot, IReadOnlyList<int> systems)
    {
        if (_routes.TryGetValue(pilot, out var previous) && previous.SetEquals(systems))
        {
            return;
        }
        if (previous is not null)
        {
            foreach (int system in previous)
            {
                _systemAgents.GetValueOrDefault(system)?.Remove(pilot);
            }
        }
        var route = systems.ToHashSet();
        _routes[pilot] = route;
        foreach (int system in route)
        {
            if (!_systemAgents.TryGetValue(system, out var members))
            {
                members = [];
                _systemAgents.Add(system, members);
            }
            members.Add(pilot);
        }
    }
}
