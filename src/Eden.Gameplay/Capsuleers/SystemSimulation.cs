namespace Eden.Gameplay.Capsuleers;

public enum SystemSimulationMode
{
    Background,
    Activating,
    Active,
    Deactivating,
}

public sealed record SystemSimulationState(int SystemId, SystemSimulationMode Mode, long Generation, long SettledTicks);

// Modes describe observers, never a second copy of a pilot or its paid work.
// The registry is owned by the same coordinator as the authoritative job queue.
public sealed class SystemSimulationRegistry
{
    private readonly Dictionary<int, SystemSimulationState> _systems = [];

    public void Register(int systemId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(systemId);
        _systems.TryAdd(systemId, new(systemId, SystemSimulationMode.Background, 0, 0));
    }

    public IReadOnlyList<SystemSimulationState> Read() => _systems.Values.OrderBy(s => s.SystemId).ToArray();

    public SystemSimulationState Get(int systemId) =>
        _systems.GetValueOrDefault(systemId) ?? new(systemId, SystemSimulationMode.Background, 0, 0);

    public IReadOnlyList<SystemSimulationState> Begin(IReadOnlyList<int> activeSystems)
    {
        var active = activeSystems.ToHashSet();
        List<SystemSimulationState> changed = [];
        foreach (int system in _systems.Keys.Union(active).Order().ToArray())
        {
            var previous = Get(system);
            bool wasActive = previous.Mode is SystemSimulationMode.Active or SystemSimulationMode.Activating;
            if (active.Contains(system) == wasActive)
            {
                continue;
            }
            var next = previous with
            {
                Mode = wasActive ? SystemSimulationMode.Deactivating : SystemSimulationMode.Activating,
                Generation = checked(previous.Generation + 1),
            };
            _systems[system] = next;
            changed.Add(next);
        }
        return changed;
    }

    public void Settle(int systemId, long throughTicks)
    {
        var state = Get(systemId);
        ArgumentOutOfRangeException.ThrowIfLessThan(throughTicks, state.SettledTicks);
        _systems[systemId] = state with
        {
            Mode = state.Mode switch
            {
                SystemSimulationMode.Activating => SystemSimulationMode.Active,
                SystemSimulationMode.Deactivating => SystemSimulationMode.Background,
                _ => state.Mode,
            },
            SettledTicks = throughTicks,
        };
    }
}

public interface ICapsuleerHandoff
{
    IReadOnlyList<SystemSimulationState> Systems();
    void Synchronize(Action action);
    ValueTask SynchronizeAsync(Action action, CancellationToken cancellationToken = default);
    CapsuleerBatch Capture();
}

public interface ICapsuleerPresentation
{
    void Refresh();
}
