using Eden.Gameplay.Combat;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Industry;
using Eden.Protocol.Loot;
using Eden.Protocol.Mining;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using static Eden.Protocol.PilotSessions.PilotSessionWire;

namespace Eden.Protocol.PilotSessions;

internal sealed class PilotSessionOutput(
    PilotCallContext context,
    long accountId,
    long clientId,
    Func<object?, Task> write,
    Action<string> report
)
{
    public Task WriteAsync(object? packet) => write(packet);

    public async Task AnnounceTransitionAsync(DockedPilot previous)
    {
        if (previous.ShipId != context.Pilot.ShipId)
        {
            // HangarView needs new inventory rows before its ship delta. In
            // space, load the capsule ball before the client adopts its ego.
            await FlushNotificationsAsync();
            if (previous.Position is not null)
            {
                await FlushFlightUpdatesAsync();
            }
        }
        if (previous.LocationId != context.Pilot.LocationId)
        {
            context.ReleaseLocationReferences();
        }
        await write(new PilotSessionChange(accountId, clientId, context.Pilot, previous).ToBlue());
        report(
            previous.ShipId != context.Pilot.ShipId
                ? $"Pilot {context.Pilot.PilotId} boarded ship {context.Pilot.ShipId} "
                    + $"from {previous.ShipId} at {context.Pilot.LocationId}."
                : $"Ship {context.Pilot.ShipId} moved from {previous.LocationId} to {context.Pilot.LocationId}."
        );
    }

    public async Task FlushFlightUpdatesAsync()
    {
        foreach (var notification in context.NotificationsBeforeFlight)
        {
            await SendNotificationAsync(notification);
        }
        context.NotificationsBeforeFlight.Clear();
        if (context.ActiveFlight is { Updates.Count: > 0 } flight)
        {
            await SendNotificationAsync(new DestinyUpdatesNotification(flight.Updates));
            flight.Updates.Clear();
        }
    }

    public async Task FlushNotificationsAsync()
    {
        foreach (var notification in context.Notifications)
        {
            await SendNotificationAsync(notification);
        }
        context.Notifications.Clear();
    }

    private Task SendNotificationAsync(PilotNotification notification)
    {
        report($"Notify {notification.Name}.");
        return write(Notification(accountId, clientId, notification));
    }
}
