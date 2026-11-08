namespace Eden.Gameplay.Capsuleers;

// The world owner serializes this state. Elapsed time is monotonic human-online time,
// never wall time, and is counted once regardless of the number of selected pilots.
public sealed class HumanActivity
{
    private readonly Dictionary<Guid, int> _sessions = [];
    private long _observedAt;
    private long _elapsedTicks;
    private long _frequency;
    private Int128 _remainder;
    private IReadOnlyList<int> _activeSystems = [];

    public HumanActivity(long elapsedTicks, long timestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedTicks);
        _elapsedTicks = elapsedTicks;
        _observedAt = timestamp;
    }

    public void Update(Guid session, int? systemId, long timestamp, long frequency)
    {
        if (systemId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(systemId));
        }
        Advance(timestamp, frequency);
        if (systemId is int system)
        {
            if (_sessions.GetValueOrDefault(session) == system)
            {
                return;
            }
            _sessions[session] = system;
        }
        else if (!_sessions.Remove(session))
        {
            return;
        }
        _activeSystems = Array.AsReadOnly(_sessions.Values.Distinct().Order().ToArray());
    }

    public HumanActivitySnapshot Read(long timestamp, long frequency)
    {
        Advance(timestamp, frequency);
        return new(_elapsedTicks, _sessions.Count, _activeSystems);
    }

    private void Advance(long timestamp, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        if (_frequency != 0 && _frequency != frequency)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency), "The timestamp frequency cannot change.");
        }
        if (timestamp < _observedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "The activity clock cannot move backwards.");
        }
        if (_sessions.Count > 0)
        {
            Int128 scaled = ((Int128)timestamp - _observedAt) * TimeSpan.TicksPerSecond + _remainder;
            _elapsedTicks = checked(_elapsedTicks + (long)(scaled / frequency));
            _remainder = scaled % frequency;
        }
        _frequency = frequency;
        _observedAt = timestamp;
    }
}
