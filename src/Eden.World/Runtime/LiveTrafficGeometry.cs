using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

internal sealed class LiveTrafficGeometry
{
    private readonly Lock _gate = new();
    private const int MaximumSystems = 1024;

    private sealed record Entry(long Catalog, IReadOnlyList<StaticSpaceObject> Objects, LinkedListNode<int> Recency);

    private readonly Dictionary<int, Entry> _systems = [];
    private readonly LinkedList<int> _systemOrder = new();
    private readonly Dictionary<int, IReadOnlyList<StaticSpaceObject>> _static = [];
    private readonly Queue<int> _staticOrder = new();
    private long _staticCatalog;
    private const int MaximumStaticObjects = 48 * 1024;
    private int _staticObjects;
    private readonly Dictionary<long, int> _belts = [];
    internal long Revision { get; private set; }

    internal IReadOnlyList<StaticSpaceObject> StaticObjects(CatalogDbContext catalog, int system)
    {
        long generation = catalog.Definitions.Identity.Generation;
        lock (_gate)
        {
            if (_staticCatalog == generation && _static.TryGetValue(system, out var cached))
            {
                return cached;
            }
        }
        var loaded = SystemObjects.Geometry(catalog, system);
        if (loaded.Count > MaximumStaticObjects)
        {
            return loaded;
        }
        lock (_gate)
        {
            if (_staticCatalog > generation)
            {
                return loaded;
            }
            if (_staticCatalog != generation)
            {
                _static.Clear();
                _staticOrder.Clear();
                _staticObjects = 0;
                _staticCatalog = generation;
            }
            if (_static.TryGetValue(system, out var cached))
            {
                return cached;
            }
            while (_static.Count >= MaximumSystems || _staticObjects + loaded.Count > MaximumStaticObjects)
            {
                int expired = _staticOrder.Dequeue();
                _staticObjects -= _static[expired].Count;
                _static.Remove(expired);
            }
            _static.Add(system, loaded);
            _staticOrder.Enqueue(system);
            _staticObjects += loaded.Count;
            return loaded;
        }
    }

    internal bool TryRead(int system, long catalog, out IReadOnlyList<StaticSpaceObject> objects)
    {
        lock (_gate)
        {
            if (_systems.TryGetValue(system, out var entry) && entry.Catalog == catalog)
            {
                Touch(entry.Recency);
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
            var snapshot = Array.AsReadOnly(objects.ToArray());
            LinkedListNode<int> recency;
            if (_systems.TryGetValue(system, out var current))
            {
                recency = current.Recency;
                Touch(recency);
            }
            else
            {
                if (_systems.Count == MaximumSystems)
                {
                    RemoveSystem(_systemOrder.First!.Value);
                }
                recency = _systemOrder.AddLast(system);
            }
            _systems[system] = new(catalog, snapshot, recency);
            foreach (int belt in belts)
            {
                _belts[belt] = system;
            }
        }
    }

    private void Touch(LinkedListNode<int> recency)
    {
        if (recency.Next is not null)
        {
            _systemOrder.Remove(recency);
            _systemOrder.AddLast(recency);
        }
    }

    private void RemoveSystem(int system)
    {
        // Belt ownership outlives a view. A later renewal can still invalidate just that system.
        if (_systems.Remove(system, out var entry))
        {
            _systemOrder.Remove(entry.Recency);
        }
    }

    private void ClearSystems()
    {
        _systems.Clear();
        _systemOrder.Clear();
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
                    ClearSystems();
                    _belts.Clear();
                }
                else if (
                    change.Table == "WorldAsteroidGeometry"
                    && change.System is int changedSystem
                    && !MissionMiningRules.IsDeposit(change.Id)
                )
                {
                    Revision++;
                    RemoveSystem(changedSystem);
                }
                else if (
                    change.Table == "WorldBeltRenewal"
                    || change.Table == "WorldAsteroid" && !change.Resident && !MissionMiningRules.IsDeposit(change.Id)
                )
                {
                    Revision++;
                    long belt =
                        change.Table == "WorldBeltRenewal"
                            ? change.Id
                            : (change.Id - AsteroidDefinitions.FirstAsteroidId) / AsteroidDefinitions.IdsPerBelt;
                    if (_belts.TryGetValue(belt, out int system))
                    {
                        RemoveSystem(system);
                    }
                    else
                    {
                        ClearSystems();
                    }
                }
            }
        }
    }
}
