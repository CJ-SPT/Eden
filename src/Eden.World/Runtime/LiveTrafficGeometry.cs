using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

internal sealed class LiveTrafficGeometry
{
    private readonly Lock _gate = new();
    private const int MaximumSystems = 1024;

    private sealed record Entry(long Catalog, IReadOnlyList<StaticSpaceObject> Objects);

    private readonly Dictionary<int, Entry> _systems = [];
    private readonly Dictionary<long, int> _belts = [];
    internal long Revision { get; private set; }

    internal bool TryRead(int system, long catalog, out IReadOnlyList<StaticSpaceObject> objects)
    {
        lock (_gate)
        {
            if (_systems.TryGetValue(system, out var entry) && entry.Catalog == catalog)
            {
                objects = entry.Objects;
                return true;
            }
            objects = [];
            return false;
        }
    }

    internal void TrackBelts(int system, IEnumerable<int> belts)
    {
        lock (_gate)
        {
            foreach (int belt in belts)
            {
                _belts[belt] = system;
            }
        }
    }

    internal void Publish(int system, long catalog, IEnumerable<int> belts, IReadOnlyList<StaticSpaceObject> objects)
    {
        lock (_gate)
        {
            if (_systems.Count >= MaximumSystems)
            {
                _systems.Clear();
                _belts.Clear();
            }
            _systems[system] = new(catalog, Array.AsReadOnly(objects.ToArray()));
            foreach (int belt in belts)
            {
                _belts[belt] = system;
            }
        }
    }

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes)
    {
        lock (_gate)
        {
            foreach (var change in changes)
            {
                if (change.Table == "schema")
                {
                    Revision++;
                    _systems.Clear();
                    _belts.Clear();
                }
                else if (change.Table is "WorldAsteroid" or "WorldBeltRenewal")
                {
                    Revision++;
                    long belt =
                        change.Table == "WorldBeltRenewal"
                            ? change.Id
                            : (change.Id - AsteroidDefinitions.FirstAsteroidId) / AsteroidDefinitions.IdsPerBelt;
                    if (_belts.TryGetValue(belt, out int system))
                    {
                        _systems.Remove(system);
                    }
                    else
                    {
                        _systems.Clear();
                    }
                }
            }
        }
    }
}
