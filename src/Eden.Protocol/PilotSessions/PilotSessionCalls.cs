using System.Diagnostics;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
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
    SessionDiagnostics? diagnostics = null
)
{
    private const int SlowCallMilliseconds = 250;
    private bool _initialStateSent;

    public async Task ProcessAsync(object? incoming)
    {
        using var operation = operations?.Begin();
        var packet = IncomingMachoPacket.Read(incoming);
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
        report($"Call {service}.{method}.");
        using var performance = SessionPerformance.Begin($"call {service}.{method}", diagnostics?.Trace ?? report);
        long started = Stopwatch.GetTimestamp();
        bool selected = context.Selected;
        DockedPilot? previous = context.ActiveFlight?.Pilot;
        updates.ApplyPlayerDamage(context);
        PilotCallResult result;
        using (SessionPerformance.Begin("dispatch", diagnostics?.Trace ?? report))
        {
            result = router.Call(context, service, method, call.Arguments, report, call.Keywords);
        }
        var dispatchTime = Stopwatch.GetElapsedTime(started);
        if (!selected && context.Selected)
        {
            updates.Enter(context);
        }
        using (SessionPerformance.Begin("updates", diagnostics?.Trace ?? report))
        {
            updates.Advance(context);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed.TotalMilliseconds >= SlowCallMilliseconds)
        {
            (diagnostics?.Warning ?? report)(
                $"Slow call {service}.{method}: {elapsed.TotalMilliseconds:F0} ms before reply; "
                    + $"{dispatchTime.TotalMilliseconds:F0} ms dispatch, {(elapsed - dispatchTime).TotalMilliseconds:F0} ms updates."
            );
        }
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
            // The pinned fitting service uses CustomInfo/info for a readable
            // gameplay rejection. Macho error code 0 means a bad destination.
            result = new PilotCallResult.NamedFailure("CustomInfo", new InformationErrorValues(validationError.Message));
        }
        if (result is PilotCallResult.NamedFailure userError)
        {
            // ErrorResponse params are original command, code and payload. Code 2 wraps an
            // exception stream. Named errors retain their client-specific handling,
            // including inventory's smaller-quantity retry for cargo capacity.
            await output.WriteAsync(packet.UserError(userError));
            await output.FlushFlightUpdatesAsync();
            await output.FlushNotificationsAsync();
            return;
        }
        if (result is PilotCallResult.UnsupportedCall)
        {
            await output.WriteAsync(packet.UnsupportedCall(service, method));
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

        var reply = ((PilotCallResult.Success)result).Reply;
        if (reply is TargetLockReply { Completion: { } completion })
        {
            output.DeferTargetLockReply(packet, completion);
        }
        else
        {
            await output.WriteAsync(packet.CallSucceeded(reply));
        }
        if (
            service == "beyonce"
            && method == "MachoBindObject"
            && call.IsBindingWithoutCall
            && context.ActiveFlight?.Pilot.Position is not null
        )
        {
            context.Flight.Snapshot();
        }
        await output.FlushFlightUpdatesAsync();
        await output.FlushNotificationsAsync();
        if (!_initialStateSent && service == "machoNet" && method == "GetServiceInfo")
        {
            _initialStateSent = true;
            await output.WriteAsync(InitialState(accountId, clientId, context.Roles));
        }
    }
}
