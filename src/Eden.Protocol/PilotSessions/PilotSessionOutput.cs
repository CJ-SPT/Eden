using Eden.Gameplay.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.Protocol.Transport;
using static Eden.Protocol.PilotSessions.PilotSessionWire;

namespace Eden.Protocol.PilotSessions;

internal sealed class PilotSessionOutput(
    PilotCallContext context,
    long accountId,
    long clientId,
    Func<object?, Task> write,
    Action<string> report,
    SessionDiagnostics? diagnostics = null
)
{
    private sealed record PendingTargetReply(IncomingMachoPacket Request, Task<PilotCallResult> Completion);

    private readonly List<PendingTargetReply> _targetReplies = [];

    internal void DeferTargetLockReply(IncomingMachoPacket request, Task<PilotCallResult> completion) =>
        _targetReplies.Add(new(request, completion));

    public Task WriteAsync(object? packet) => write(packet);

    internal async Task WriteCallReplyAsync(IncomingMachoPacket request, BlueModel reply)
    {
        try
        {
            await write(request.CallSucceeded(reply));
        }
        catch (OutgoingPacketSizeException error)
        {
            (diagnostics?.Warning ?? report)($"Call reply exceeded its transport limit: {error.Message}");
            await write(request.UserError(new("CustomInfo", new InformationErrorValues("The server response is too large to send."))));
        }
    }

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
        // Cancelled scan RPCs invoke native FailLockTarget. Deliver that cleanup
        // and lost-lock events while the departing ball and its bracket still exist.
        await FlushTargetRepliesAsync();
        var lostTargets = context
            .Notifications.OfType<TargetChanged>()
            .Where(n => n.Change is TargetChange.Lost or TargetChange.OtherLost)
            .ToArray();
        foreach (var notification in lostTargets)
        {
            await SendNotificationAsync(notification);
            context.Notifications.Remove(notification);
        }
        foreach (var notification in context.NotificationsBeforeFlight)
        {
            await SendNotificationAsync(notification);
        }
        context.NotificationsBeforeFlight.Clear();
        if (context.ActiveFlight is { Updates.Count: > 0 } flight)
        {
            if (Environment.GetEnvironmentVariable("EDEN_TRACE_MOTION") == "1")
            {
                foreach (var update in flight.Updates)
                {
                    (diagnostics?.Trace ?? report)(
                        $"MotionUpdate viewer={flight.Pilot.ShipId} tick={update.Stamp} command={update.Command.GetType().Name}"
                    );
                    var slims = update.Command switch
                    {
                        DestinyCommand.SetState state => state.Snapshot.Slims,
                        DestinyCommand.AddBalls added => added.Addition.Slims,
                        _ => [],
                    };
                    foreach (var slim in slims.OfType<NpcSlimItem>())
                    {
                        var npc = slim.Npc;
                        (diagnostics?.Trace ?? report)(
                            $"MotionNpc viewer={flight.Pilot.ShipId} tick={update.Stamp} id={npc.Id} type={npc.TypeId} "
                                + $"group={npc.GroupId} corp={npc.CorporationId} position={npc.Position}"
                        );
                    }
                }
                (diagnostics?.Trace ?? report)($"MotionHull viewer={flight.Pilot.ShipId} hull={flight.Pilot.Flight}");
            }
            bool hasFullState = flight.Updates.Any(update => update.Command is DestinyCommand.SetState);
            bool hasOwnGateDeparture = flight.Updates.Any(update =>
                update.Command is DestinyCommand.JumpOut jump && jump.Id == flight.Pilot.ShipId
            );
            await SendNotificationAsync(new DestinyUpdatesNotification(flight.Updates));
            if (hasOwnGateDeparture)
            {
                // Give SubwayService time to prepare the destination after sending JumpOut.
                flight.GateDeparturePublished();
            }
            if (hasFullState)
            {
                // Source scene clearing runs asynchronously in build 3662168.
                // Start its jump effect in a later tick, after this state was sent.
                flight.SourceStatePublished();
            }
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
        await FlushTargetRepliesAsync();
    }

    private async Task FlushTargetRepliesAsync()
    {
        foreach (var pending in _targetReplies.Where(p => p.Completion.IsCompleted).ToArray())
        {
            var result = await pending.Completion;
            switch (result)
            {
                case PilotCallResult.Success success:
                    await WriteCallReplyAsync(pending.Request, success.Reply);
                    break;
                case PilotCallResult.NamedFailure failure:
                    await write(pending.Request.UserError(failure));
                    break;
                default:
                    throw new InvalidOperationException("Invalid target scan completion.");
            }
            _targetReplies.Remove(pending);
        }
    }

    private Task SendNotificationAsync(PilotNotification notification)
    {
        report($"Notify {notification.Name}.");
        return write(Notification(accountId, clientId, notification));
    }
}
