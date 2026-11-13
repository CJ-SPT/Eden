using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using static Eden.Protocol.PilotSessions.PilotSessionWire;

namespace Eden.Protocol.PilotSessions;

internal sealed class PilotSessionCalls(
    PilotCallContext context,
    PilotSessionOutput output,
    PilotCallRouter router,
    PilotUpdateCoordinator updates,
    SkillHandler skills,
    TimeProvider clock,
    Action<string> report,
    long accountId,
    long clientId,
    IPilotOperationScope? operations = null,
    SessionDiagnostics? diagnostics = null,
    ICapsuleerHandoff? handoff = null,
    ICapsuleerPresentation? presentation = null
)
{
    private bool _initialStateSent;
    private readonly PilotClientCalls _clientCalls = new();

    public async Task ProcessAsync(object? incoming, long? received = null)
    {
        var packet = IncomingMachoPacket.Read(incoming);
        PilotClientCalls.Pending? resumed = null;
        object? answer = null;
        if (_clientCalls.TryTake(packet, out resumed, out answer))
        {
            packet = resumed!.Request;
        }
        if (packet.Name == PacketNamespace + "PingReq")
        {
            await output.WriteAsync(packet.Ping(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()));
            return;
        }

        if (packet.Name != PacketNamespace + "CallReq")
        {
            report($"Received {packet.Name}.");
            return;
        }

        byte[] data = packet.CallBytes();
        PilotCallRequest call;
        try
        {
            call = PilotCallRequest.Read(data);
        }
        catch (InvalidDataException error)
        {
            (diagnostics?.Warning ?? report)($"Invalid service call: {error.Message}");
            await output.WriteAsync(packet.InvalidArguments());
            return;
        }
        string service = router.ResolveService(context, packet.Destination(), call.ObjectId);
        string method = call.Method;
        // Trusted plugin code runs outside the world owner. Its typed services acquire their own operation scopes.
        PilotCallResult? modResult =
            service == "edenMods" ? router.Call(context, service, method, call.Arguments, report, call.Keywords) : null;
        using var operation = operations?.Begin();
        using var systemContext = context.Selected ? context.Flight.UseSystem() : null;
        string description = DescribeCall(service, method, call.Arguments);
        report($"Call {description}.");
        using var performance = SessionPerformance.Begin($"call {description}", diagnostics?.Trace ?? report);
        using var latency = new SessionLatency(
            $"call {description}",
            diagnostics?.Warning ?? report,
            diagnostics?.Trace ?? report,
            received
        );
        bool selected = context.Selected;
        DockedPilot? previous = context.ActiveFlight?.Pilot;
        PilotCallResult result = PilotCallResult.None;
        void Dispatch()
        {
            using (SessionLatency.Measure(SessionLatencyPhase.TrafficRefresh))
            {
                presentation?.Refresh();
            }
            using (SessionLatency.Measure(SessionLatencyPhase.PendingDamage))
            {
                updates.ApplyPlayerDamage(context);
            }
            using (SessionLatency.Measure(SessionLatencyPhase.Handler))
            using (SessionPerformance.Begin("dispatch", diagnostics?.Trace ?? report))
            {
                result = resumed is not null
                    ? context.Selected && context.Pilot.PilotId == resumed.PilotId
                        ? resumed.Dialog.Complete(answer)
                        : new PilotCallResult.Failure("The agent dialog belongs to a previous pilot session.")
                    : modResult ?? router.Call(context, service, method, call.Arguments, report, call.Keywords);
            }
            if (!selected && context.Selected)
            {
                using var entryLatency = SessionLatency.Measure(SessionLatencyPhase.Entry);
                using var entering = SessionPerformance.Begin("enter", diagnostics?.Trace ?? report);
                updates.Enter(context);
            }
            using var currentLocation = context.Selected ? context.Flight.UseSystem() : null;
            using (SessionLatency.Measure(SessionLatencyPhase.Updates))
            using (SessionPerformance.Begin("updates", diagnostics?.Trace ?? report))
            {
                updates.Advance(context);
            }
            using (SessionLatency.Measure(SessionLatencyPhase.TrafficRefresh))
            {
                presentation?.Refresh();
            }
        }
        if (handoff is not null && context.Selected)
        {
            await handoff.SynchronizeAsync(context.Pilot.SystemId, Dispatch);
        }
        else
        {
            Dispatch();
        }
        using var outputLatency = SessionLatency.Measure(SessionLatencyPhase.Output);
        using var outputPerformance = SessionPerformance.Begin("output", diagnostics?.Trace ?? report);
        // A timer-driven loss can happen while rejecting an unrelated RPC.
        // Its durable capsule transition still has to reach the client.
        if (previous is not null && !context.Selected)
        {
            await output.WriteAsync(new PilotSessionChange(accountId, clientId, null, previous).ToBlue());
            (diagnostics?.Information ?? report)(
                $"Logged off pilot {previous.PilotId}; returned account {accountId} to character selection."
            );
        }
        else if (
            previous is not null
            && (
                previous.LocationId != context.Pilot.LocationId
                || previous.ShipId != context.Pilot.ShipId
                || previous.CorpId != context.Pilot.CorpId
                || previous.Identity.Fleet != context.Pilot.Identity.Fleet
                || previous.Identity.CorporationRoles != context.Pilot.Identity.CorporationRoles
                || previous.Identity.CorporationAccess != context.Pilot.Identity.CorporationAccess
                || previous.Identity.CorporationAccountKey != context.Pilot.Identity.CorporationAccountKey
            )
        )
        {
            await output.AnnounceTransitionAsync(previous);
        }
        if (result is PilotCallResult.Failure validationError)
        {
            // The in-game client presents CustomNotify through its text overlay.
            // Character selection and explicit information prompts use CustomInfo.
            result =
                !selected || validationError.ShowInformationDialog
                    ? new PilotCallResult.NamedFailure("CustomInfo", new InformationErrorValues(validationError.Message))
                    : new PilotCallResult.NamedFailure("CustomNotify", new NotificationErrorValues(validationError.Message));
        }
        if (result is PilotCallResult.NamedFailure userError)
        {
            // ErrorResponse params are original command, code and payload. Code 2 wraps an
            // exception stream. Named errors retain their client-specific handling,
            // including inventory's smaller-quantity retry for cargo capacity.
            await output.WriteAsync(packet.UserError(userError));
            latency.ReplyWritten();
            await output.FlushFlightUpdatesAsync();
            await output.FlushNotificationsAsync();
            return;
        }
        if (result is PilotCallResult.UnsupportedCall)
        {
            await output.WriteAsync(packet.UnsupportedCall(service, method));
            latency.ReplyWritten();
            await output.FlushFlightUpdatesAsync();
            await output.FlushNotificationsAsync();
            return;
        }
        if (!selected && context.Selected)
        {
            var pilot = context.Pilot;
            skills.Advance(context);
            await output.WriteAsync(new PilotSessionChange(accountId, clientId, pilot).ToBlue());
            (diagnostics?.Information ?? report)(
                $"Accepted selection of {pilot.Name} ({pilot.PilotId}) at {pilot.LocationId} in ship {pilot.ShipId}."
            );
        }

        if (result is PilotCallResult.ClientCall clientCall)
        {
            await output.WriteAsync(_clientCalls.Begin(packet, clientCall.Dialog, accountId, clientId, context.Pilot.PilotId));
            await output.FlushFlightUpdatesAsync();
            await output.FlushNotificationsAsync();
            return;
        }
        var reply = ((PilotCallResult.Success)result).Reply;
        if (reply is TargetLockReply { Completion: { } completion })
        {
            output.DeferTargetLockReply(packet, completion);
        }
        else
        {
            await output.WriteCallReplyAsync(packet, reply);
            latency.ReplyWritten();
        }
        if (
            service == "beyonce"
            && method == "MachoBindObject"
            && call.IsBindingWithoutCall
            && context.ActiveFlight?.Pilot.Position is not null
        )
        {
            if (handoff is not null)
            {
                await handoff.SynchronizeAsync(
                    context.Pilot.SystemId,
                    () =>
                    {
                        using (SessionLatency.Measure(SessionLatencyPhase.TrafficRefresh))
                        {
                            presentation?.Refresh();
                        }
                        context.Flight.Snapshot();
                    }
                );
            }
            else
            {
                context.Flight.Snapshot();
            }
        }
        await output.FlushFlightUpdatesAsync();
        await output.FlushNotificationsAsync();
        if (!_initialStateSent && service == "machoNet" && method == "GetServiceInfo")
        {
            _initialStateSent = true;
            await output.WriteAsync(InitialState(accountId, clientId, context.Roles));
        }
    }

    internal static string DescribeCall(string service, string method, object? arguments)
    {
        return
            method == "MachoBindObject"
            && arguments is object?[] { Length: 2 } args
            && args[1] is object?[] { Length: >= 2 } embedded
            && embedded[0] is string or byte[]
            ? $"{service}.{method} ({BlueMarshal.Text(embedded[0])})"
            : $"{service}.{method}";
    }
}
