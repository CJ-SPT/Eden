namespace Eden.Gameplay.Capsuleers;

public sealed record HumanActivityClock(long ElapsedTicks, long Timestamp, long Frequency, long Remainder, bool Active)
{
    public long ElapsedAt(long timestamp)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timestamp, Timestamp);
        return !Active
            ? ElapsedTicks
            : checked(ElapsedTicks + (long)((((Int128)timestamp - Timestamp) * TimeSpan.TicksPerSecond + Remainder) / Frequency));
    }
}

// The world owner serializes this state. Elapsed time is monotonic human-online time,
// never wall time, and is counted once regardless of the number of selected pilots.
public sealed class HumanActivity
{
    private readonly Dictionary<Guid, SelectedHumanPilot> _sessions = [];
    private long _observedAt;
    private long _elapsedTicks;
    private long _frequency;
    private Int128 _remainder;
    private long _resumes;
    private IReadOnlyList<int> _activeSystems = [];

    private IReadOnlyList<SelectedHumanPilot> _pilots = [];

    public HumanActivity(long elapsedTicks, long timestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedTicks);
        _elapsedTicks = elapsedTicks;
        _observedAt = timestamp;
    }

    public void Update(Guid session, int? systemId, long timestamp, long frequency, long pilotId = 0)
    {
        if (systemId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(systemId));
        }
        Advance(timestamp, frequency);
        if (systemId is int system)
        {
            var selected = new SelectedHumanPilot(pilotId, system);
            if (_sessions.GetValueOrDefault(session) == selected)
            {
                return;
            }
            if (_sessions.Count == 0)
            {
                _resumes = checked(_resumes + 1);
            }
            _sessions[session] = selected;
        }
        else if (!_sessions.Remove(session))
        {
            return;
        }
        _pilots = Array.AsReadOnly(_sessions.Values.Where(p => p.PilotId > 0).Distinct().OrderBy(p => p.PilotId).ToArray());
        _activeSystems = Array.AsReadOnly(_sessions.Values.Select(p => p.SystemId).Distinct().Order().ToArray());
    }

    public HumanActivitySnapshot Read(long timestamp, long frequency)
    {
        Advance(timestamp, frequency);
        return new(_elapsedTicks, _sessions.Count, _activeSystems, _resumes) { Pilots = _pilots };
    }

    public HumanActivityClock CaptureClock() => new(_elapsedTicks, _observedAt, _frequency, (long)_remainder, _sessions.Count > 0);

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
