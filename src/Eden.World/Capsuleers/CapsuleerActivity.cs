using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
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
internal sealed class HumanPresence(WorldDatabase database, TimeProvider clock) : IHumanPresence
{
    private readonly Lock _gate = new();
    private readonly CapsuleerActivity _standalone = new(database, clock);

    public IHumanPresenceLease Select(long pilotId, int systemId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pilotId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(systemId);
        Guid session = Guid.NewGuid();
        Update(session, systemId);
        return new Lease(this, session);
    }

    public HumanActivitySnapshot Read()
    {
        var live = LiveWorld.Find(database.Path);
        using var owner = live?.Enter();
        lock (_gate)
        {
            var activity = live?.CapsuleerActivity ?? _standalone;
            return activity.Read();
        }
    }

    public void Update(Guid session, int? systemId)
    {
        var live = LiveWorld.Find(database.Path);
        using var owner = live?.Enter();
        lock (_gate)
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

    private sealed class Lease(HumanPresence presence, Guid session) : IHumanPresenceLease
    {
        private readonly Lock _gate = new();
        private bool _disposed;

        public void Transfer(int systemId)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(systemId);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                presence.Update(session, systemId);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    presence.Update(session, null);
                    _disposed = true;
                }
            }
        }
    }
}
