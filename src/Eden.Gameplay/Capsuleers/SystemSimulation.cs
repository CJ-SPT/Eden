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
    private readonly HashSet<int> _observed = [];
    private long _settledTicks;
    private long _latestSystemTicks;
    public int Count => _systems.Count;

    public void Register(int systemId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(systemId);
        _systems.TryAdd(systemId, new(systemId, SystemSimulationMode.Background, 0, 0));
    }

    public IReadOnlyList<SystemSimulationState> Read() => _systems.Keys.Order().Select(Get).ToArray();

    public IReadOnlyList<SystemSimulationState> Observed() => _observed.Order().Select(Get).ToArray();

    public void SettleAll(long throughTicks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(throughTicks, Math.Max(_settledTicks, _latestSystemTicks));
        _settledTicks = throughTicks;
        foreach (int system in _observed.ToArray())
        {
            Settle(system, throughTicks);
        }
    }

    public SystemSimulationState Get(int systemId)
    {
        var state = _systems.GetValueOrDefault(systemId) ?? new(systemId, SystemSimulationMode.Background, 0, 0);
        return state.SettledTicks >= _settledTicks ? state : state with { SettledTicks = _settledTicks };
    }

    public IReadOnlyList<SystemSimulationState> Begin(IReadOnlyList<int> activeSystems)
    {
        var active = activeSystems.ToHashSet();
        List<SystemSimulationState> changed = [];
        foreach (int system in _observed.Union(active).Order().ToArray())
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
            _observed.Add(system);
            changed.Add(next);
        }
        return changed;
    }

    public void Settle(int systemId, long throughTicks)
    {
        var state = Get(systemId);
        ArgumentOutOfRangeException.ThrowIfLessThan(throughTicks, state.SettledTicks);
        _latestSystemTicks = Math.Max(_latestSystemTicks, throughTicks);
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
        if (_systems[systemId].Mode == SystemSimulationMode.Background)
        {
            _observed.Remove(systemId);
        }
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
