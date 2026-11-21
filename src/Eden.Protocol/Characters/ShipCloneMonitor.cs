using System.Collections.Concurrent;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Characters;

[Injectable(InjectionType.Singleton)]
internal sealed class ShipCloneSignal
{
    internal event Action<long, PilotNotification>? Published;

    internal void Send(long pilotId, PilotNotification notice) => Published?.Invoke(pilotId, notice);
}

[Injectable(InjectionType.Scoped)]
internal sealed class ShipCloneMonitor : IDisposable
{
    private readonly ShipCloneSignal _signal;
    private readonly ConcurrentQueue<(long PilotId, PilotNotification Notice)> _pending = new();
    private long _pilot;

    public ShipCloneMonitor(ShipCloneSignal signal)
    {
        _signal = signal;
        _signal.Published += Receive;
    }

    internal void Advance(PilotCallContext context)
    {
        long pilot = context.Selected ? context.Pilot.PilotId : 0;
        Volatile.Write(ref _pilot, pilot);
        while (_pending.TryDequeue(out var delivery))
        {
            if (delivery.PilotId == pilot)
            {
                context.Notifications.Add(delivery.Notice);
            }
        }
    }

    private void Receive(long pilotId, PilotNotification notice)
    {
        if (Volatile.Read(ref _pilot) == pilotId)
        {
            _pending.Enqueue((pilotId, notice));
        }
    }

    public void Dispose() => _signal.Published -= Receive;
}

internal sealed record ShipCloneOfferChanged(string Event, ShipCloneOffer Offer) : PilotNotification(Event)
{
    public override object?[] Arguments => [new object?[] { Offer.HostId, Offer.PilotId, Offer.ShipId, 0 }];
}

internal sealed record ShipCloneCacheInvalidated(long ShipId, long PilotId) : PilotNotification("OnShipJumpCloneCacheInvalidated")
{
    public override object?[] Arguments => [ShipId, PilotId];
}
