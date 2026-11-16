using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Sovereignty;

[Injectable(InjectionType.Scoped)]
internal sealed class SovereigntyMonitor(IPilotEntosis entosis)
{
    private int _system;
    private long? _hub;
    private SovereigntyClaim? _claim;

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected || !entosis.Available)
        {
            return;
        }
        int system = context.Pilot.SystemId;
        var current = entosis.Hub(system);
        bool changed = _system == system && (_hub != current?.HubId || _claim != current?.Claim);
        _system = system;
        _hub = current?.HubId;
        _claim = current?.Claim;
        if (changed)
        {
            context.Notifications.Add(new SovereigntyStructuresUpdated(system, current));
            context.Notifications.Add(new SovereigntyStatusChanged(system, current?.Claim));
        }
    }
}
