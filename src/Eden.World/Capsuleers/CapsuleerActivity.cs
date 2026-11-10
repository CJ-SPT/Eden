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
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Changed => _changed.Task;

    internal void Wake()
    {
        var previous = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

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

    internal void Update(Guid session, int? system, long pilotId) =>
        Activity.Update(session, system, clock.GetTimestamp(), clock.TimestampFrequency, pilotId);

    internal HumanActivitySnapshot Read() => Activity.Read(clock.GetTimestamp(), clock.TimestampFrequency);

    internal CapsuleerClockCheckpoint? Capture() => _activity is null ? null : new(Read().ElapsedTicks);
}

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerActivitySource(WorldDatabase database, TimeProvider clock, IWorldSynchronization synchronization)
{
    private readonly CapsuleerActivity _standalone = new(database, clock);
    private TaskCompletionSource _membershipChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task MembershipChanged => Volatile.Read(ref _membershipChanged).Task;

    internal void RequestPreparation()
    {
        var previous = Interlocked.Exchange(ref _membershipChanged, new(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
    }

    // Scheduling reads this under the same owner as the queue and activity clock.
    internal Task Changed => (LiveWorld.Find(database.Path)?.CapsuleerActivity ?? _standalone).Changed;

    internal void Wake() => (LiveWorld.Find(database.Path)?.CapsuleerActivity ?? _standalone).Wake();

    public HumanActivitySnapshot Read()
    {
        var live = LiveWorld.Find(database.Path);
        using var owner = live?.EnterShared();
        using (synchronization.Enter("CapsuleerActivity"))
        {
            var activity = live?.CapsuleerActivity ?? _standalone;
            return activity.Read();
        }
    }

    public void Update(Guid session, int? systemId, long pilotId = 0)
    {
        var live = LiveWorld.Find(database.Path);
        using var owner = live?.EnterShared();
        using (synchronization.Enter("CapsuleerActivity"))
        {
            var activity = live?.CapsuleerActivity ?? _standalone;
            activity.Update(session, systemId, pilotId);
            activity.Wake();
            var previous = _membershipChanged;
            _membershipChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
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
        Change(session, systemId, () => true, pilotId: pilotId);
        return new Lease(this, session, systemId, pilotId);
    }

    public HumanActivitySnapshot Read() => activity.Read();

    public void Update(Guid session, int? systemId) => Change(session, systemId, () => true);

    private void Change(
        Guid session,
        int? systemId,
        Func<bool> validate,
        Action? committed = null,
        int? previousSystem = null,
        long pilotId = 0
    )
    {
        int target;
        int? original;
        using (synchronization.Enter("CapsuleerActivity"))
        {
            original = _sessions.TryGetValue(session, out int saved) ? saved : null;
            target = systemId ?? previousSystem ?? _sessions.GetValueOrDefault(session);
        }
        void Apply()
        {
            using var ownership = synchronization.Enter("human membership");
            if (validate())
            {
                activity.Update(session, systemId, pilotId);
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
            using (synchronization.Enter("CapsuleerActivity"))
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
                using (synchronization.Enter("CapsuleerActivity"))
                {
                    activity.Update(session, original, pilotId);
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
        using (synchronization.Enter("CapsuleerActivity"))
        {
            activity.Update(session, null);
            _sessions.Remove(session);
            committed();
        }
    }

    private sealed class Lease(HumanPresence presence, Guid session, int initialSystem, long pilotId) : IHumanPresenceLease
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
                },
                pilotId: pilotId
            );
            _system = systemId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                presence.Change(session, null, () => !_disposed, () => _disposed = true, _system, pilotId);
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
