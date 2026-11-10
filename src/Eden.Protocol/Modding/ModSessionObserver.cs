using Eden.Modding;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Modding;

internal sealed class ModSessionObserver(PilotCallContext context, IModSessionEvents? events) : IDisposable
{
    private ModPilot? _previous;

    internal static ModPilot Pilot(PilotCallContext context) =>
        new(
            context.AccountId,
            context.Pilot.PilotId,
            context.Roles,
            context.Pilot.Position is null && !context.Pilot.InStructure ? context.Pilot.NpcStationId : null,
            context.Pilot.SystemId
        );

    public void Observe()
    {
        if (events is null)
        {
            return;
        }
        if (
            _previous is { } unchanged
            && context.Selected
            && unchanged.PilotId == context.Pilot.PilotId
            && unchanged.StationId == (context.Pilot.Position is null && !context.Pilot.InStructure ? context.Pilot.NpcStationId : null)
            && unchanged.SolarSystemId == context.Pilot.SystemId
            && unchanged.Roles == context.Roles
        )
        {
            return;
        }
        var current = context.Selected ? Pilot(context) : null;
        if (_previous is { } previous && current?.PilotId != previous.PilotId)
        {
            events.Publish(new(ModEventKind.PilotDisconnected, previous));
        }
        if (current is not null && _previous?.PilotId != current.PilotId)
        {
            events.Publish(new(ModEventKind.PilotSelected, current));
        }
        else if (current is not null && (_previous?.StationId != current.StationId || _previous?.SolarSystemId != current.SolarSystemId))
        {
            events.Publish(new(ModEventKind.LocationChanged, current));
        }
        _previous = current;
    }

    public void Dispose()
    {
        if (_previous is { } previous)
        {
            _previous = null;
            events?.Publish(new(ModEventKind.PilotDisconnected, previous));
        }
    }
}
