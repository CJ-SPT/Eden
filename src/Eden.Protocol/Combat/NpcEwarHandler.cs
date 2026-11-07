using Eden.Gameplay.Combat;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Scoped)]
internal sealed class NpcEwarHandler(
    TimeProvider clock,
    INpcElectronicWarfare warfare,
    SupportModuleWire attributes,
    IPilotShipResources resources
)
{
    private NpcEwarContact[] _received = [];
    private long _shipId;

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        if (_shipId != 0 && _shipId != context.Pilot.ShipId)
        {
            Leave(context);
        }
        _shipId = context.Pilot.ShipId;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        NpcEwarContact[] incoming;
        if (context.Flight.CanTarget)
        {
            incoming = warfare.Advance(context.Pilot.PilotId, _shipId, now).ToArray();
        }
        else
        {
            warfare.Cancel(_shipId);
            incoming = [];
        }
        var ended = _received.Where(e => !incoming.Any(n => SameSource(e, n))).ToArray();
        var started = incoming.Where(e => !_received.Any(n => SameSource(e, n))).ToArray();
        foreach (var effect in ended)
        {
            context.NotificationsBeforeFlight.Add(new NpcEwarChanged(effect, false));
        }
        foreach (var effect in started)
        {
            context.NotificationsBeforeFlight.Add(new NpcEwarChanged(effect, true));
        }
        bool drained = incoming.Any(e =>
            e.Ability.Kind == NpcEwarKind.EnergyNeutralization && e.Applied.CapacitorDrain > 0 && !_received.Contains(e)
        );
        if (ended.Length > 0 || started.Length > 0 || drained)
        {
            context.Flight.RefreshTackle(true);
            attributes.Refresh(context, drained ? resources.Capacitor(context.Pilot.PilotId, now) : null);
        }
        if (!_received.SequenceEqual(incoming))
        {
            context.Flight.RefreshNpcs();
        }
        _received = incoming;
    }

    private static bool SameSource(NpcEwarContact first, NpcEwarContact second) =>
        first.SourceId == second.SourceId && first.Ability.Kind == second.Ability.Kind && first.TargetShipId == second.TargetShipId;

    public void Leave(PilotCallContext context)
    {
        if (_shipId != 0)
        {
            warfare.Cancel(_shipId);
        }
        foreach (var effect in _received)
        {
            context.NotificationsBeforeFlight.Add(new NpcEwarChanged(effect, false));
        }
        _received = [];
        _shipId = 0;
    }
}
