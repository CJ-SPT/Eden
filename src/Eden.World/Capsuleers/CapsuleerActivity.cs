using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerClockCheckpoint(long ElapsedTicks)
{
    internal void Apply(WorldDbContext world) =>
        world.Database.ExecuteSql($"UPDATE WorldCapsuleerClock SET ElapsedTicks={ElapsedTicks} WHERE Id=1");
}

// All entry points, including checkpoint capture, are serialized by the world owner.
internal sealed class CapsuleerActivity(WorldDatabase database, TimeProvider clock)
{
    private HumanActivity? _activity;

    private HumanActivity Activity
    {
        get
        {
            if (_activity is null)
            {
                using WorldDbContext world = new(database.Path);
                long elapsed = world.Set<SavedCapsuleerClock>().AsNoTracking().Single().ElapsedTicks;
                _activity = new(elapsed, clock.GetTimestamp());
            }
            return _activity;
        }
    }

    internal void Update(Guid session, int? system) => Activity.Update(session, system, clock.GetTimestamp(), clock.TimestampFrequency);

    internal HumanActivitySnapshot Read() => Activity.Read(clock.GetTimestamp(), clock.TimestampFrequency);

    internal CapsuleerClockCheckpoint? Capture() => _activity is null ? null : new(Read().ElapsedTicks);
}

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerActivitySource(WorldDatabase database, TimeProvider clock, IWorldSynchronization synchronization)
{
    private readonly CapsuleerActivity _standalone = new(database, clock);

    public HumanActivitySnapshot Read()
    {
        var live = LiveWorld.Find(database.Path);
        using var owner = live?.Enter();
        lock (synchronization.Gate)
        {
            var activity = live?.CapsuleerActivity ?? _standalone;
            return activity.Read();
        }
    }

    public void Update(Guid session, int? systemId)
    {
        var live = LiveWorld.Find(database.Path);
        using var owner = live?.Enter();
        lock (synchronization.Gate)
        {
            var activity = live?.CapsuleerActivity ?? _standalone;
            activity.Update(session, systemId);
            if (live is not null)
            {
                live.ObjectsChanged();
            }
            else
            {
                using WorldDbContext world = new(database.Path);
                activity.Capture()!.Apply(world);
            }
        }
    }
}

[Injectable(InjectionType.Singleton)]
internal sealed class HumanPresence(CapsuleerActivitySource activity, ICapsuleerHandoff handoff, IWorldSynchronization synchronization)
    : IHumanPresence
{
    private readonly Dictionary<Guid, int> _sessions = [];

    public IHumanPresenceLease Select(long pilotId, int systemId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pilotId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(systemId);
        Guid session = Guid.NewGuid();
        Update(session, systemId);
        return new Lease(this, session, systemId);
    }

    public HumanActivitySnapshot Read() => activity.Read();

    public void Update(Guid session, int? systemId) => Change(session, systemId, () => true);

    private void Change(Guid session, int? systemId, Func<bool> validate, Action? committed = null, int? previousSystem = null)
    {
        int target;
        int? original;
        lock (synchronization.Gate)
        {
            original = _sessions.TryGetValue(session, out int saved) ? saved : null;
            target = systemId ?? previousSystem ?? _sessions.GetValueOrDefault(session);
        }
        void Apply()
        {
            if (validate())
            {
                activity.Update(session, systemId);
                if (systemId is { } selected)
                {
                    _sessions[session] = selected;
                }
                else
                {
                    _sessions.Remove(session);
                }
                committed?.Invoke();
            }
        }
        if (target == 0)
        {
            lock (synchronization.Gate)
            {
                Apply();
            }
        }
        else
        {
            try
            {
                handoff.Synchronize(target, Apply);
            }
            catch
            {
                lock (synchronization.Gate)
                {
                    activity.Update(session, original);
                    if (original is { } previous)
                    {
                        _sessions[session] = previous;
                    }
                    else
                    {
                        _sessions.Remove(session);
                    }
                }
                throw;
            }
        }
    }

    private void Forget(Guid session, Action committed)
    {
        lock (synchronization.Gate)
        {
            activity.Update(session, null);
            _sessions.Remove(session);
            committed();
        }
    }

    private sealed class Lease(HumanPresence presence, Guid session, int initialSystem) : IHumanPresenceLease
    {
        private bool _disposed;
        private int _system = initialSystem;

        public void Transfer(int systemId)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(systemId);
            presence.Change(
                session,
                systemId,
                () =>
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return true;
                }
            );
            _system = systemId;
        }

        public void Dispose()
        {
            try
            {
                presence.Change(session, null, () => !_disposed, () => _disposed = true, _system);
            }
            catch
            {
                // A failed due action must not retain a disconnected human and
                // leave the economic clock running without a selected session.
                presence.Forget(session, () => _disposed = true);
                throw;
            }
        }
    }
}
