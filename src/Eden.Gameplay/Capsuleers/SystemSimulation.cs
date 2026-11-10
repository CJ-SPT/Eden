namespace Eden.Gameplay.Capsuleers;

public enum SystemSimulationMode
{
    Background,
    Activating,
    Active,
    Deactivating,
}

public enum SystemSimulationTier
{
    Uncreated,
    RemoteScheduled,
    Detailed,
}

public sealed record SystemSimulationState(int SystemId, SystemSimulationMode Mode, long Generation, long SettledTicks)
{
    public bool Scheduled { get; init; } = true;
    public bool Pinned { get; init; }
    public SystemSimulationTier Tier =>
        Pinned || Mode is SystemSimulationMode.Active or SystemSimulationMode.Activating ? SystemSimulationTier.Detailed
        : Scheduled ? SystemSimulationTier.RemoteScheduled
        : SystemSimulationTier.Uncreated;
}

// Modes describe observers, never a second copy of a pilot or its paid work.
// The registry is owned by the same coordinator as the authoritative job queue.
public sealed class SystemSimulationRegistry
{
    private readonly Dictionary<int, SystemSimulationState> _systems = [];
    private readonly HashSet<int> _observed = [];
    private long _settledTicks;
    public int Count => _systems.Count;

    public void Register(int systemId, bool scheduled = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(systemId);
        _systems.TryAdd(systemId, new(systemId, SystemSimulationMode.Background, 0, 0) { Scheduled = scheduled });
        if (scheduled)
        {
            _systems[systemId] = _systems[systemId] with { Scheduled = true };
        }
    }

    public IReadOnlyList<SystemSimulationState> Read() => _systems.Keys.Order().Select(Get).ToArray();

    public IReadOnlyList<SystemSimulationState> Observed() => _observed.Order().Select(Get).ToArray();

    public void SettleAll(long throughTicks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(throughTicks, _settledTicks);
        _settledTicks = throughTicks;
        foreach (int system in _observed.ToArray())
        {
            Settle(system, Math.Max(Get(system).SettledTicks, throughTicks));
        }
    }

    public SystemSimulationState Get(int systemId)
    {
        var state = _systems.GetValueOrDefault(systemId) ?? new(systemId, SystemSimulationMode.Background, 0, 0) { Scheduled = false };
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
                Scheduled = true,
                Mode = wasActive ? SystemSimulationMode.Deactivating : SystemSimulationMode.Activating,
                Generation = checked(previous.Generation + 1),
            };
            _systems[system] = next;
            _observed.Add(system);
            changed.Add(next);
        }
        return changed;
    }

    public void Pin(int systemId, bool pinned)
    {
        var state = Get(systemId);
        _systems[systemId] = state with { Pinned = pinned };
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
    void Synchronize(int systemId, Action action) => Synchronize(action);
    ValueTask SynchronizeAsync(Action action, CancellationToken cancellationToken = default);
    ValueTask SynchronizeAsync(int systemId, Action action, CancellationToken cancellationToken = default) =>
        SynchronizeAsync(action, cancellationToken);
    CapsuleerBatch Capture();
}

public interface ICapsuleerPresentation
{
    void Refresh();
}
