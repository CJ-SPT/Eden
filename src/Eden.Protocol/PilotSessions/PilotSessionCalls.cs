using System.Diagnostics;
using Eden.Gameplay.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Protocol.Skills;
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
    long clientId
)
{
    private const int SlowCallMilliseconds = 250;
    private bool _initialStateSent;

    public async Task ProcessAsync(object? incoming)
    {
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
            report(error.Message);
            await output.WriteAsync(packet.InvalidArguments());
            return;
        }
        string service = router.ResolveService(context, packet.Destination(), call.ObjectId);
        string method = call.Method;
        report($"Call {service}.{method}.");
        using var performance = SessionPerformance.Begin($"call {service}.{method}", report);
        long started = Stopwatch.GetTimestamp();
        bool selected = context.Selected;
        DockedPilot? previous = context.ActiveFlight?.Pilot;
        PilotCallResult result;
        using (SessionPerformance.Begin("dispatch", report))
        {
            result = router.Call(context, service, method, call.Arguments, report, call.Keywords);
        }
        var dispatchTime = Stopwatch.GetElapsedTime(started);
        if (!selected && context.Selected)
        {
            updates.Enter(context);
        }
        using (SessionPerformance.Begin("updates", report))
        {
            updates.Advance(context);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed.TotalMilliseconds >= SlowCallMilliseconds)
        {
            report(
                $"Slow call {service}.{method}: {elapsed.TotalMilliseconds:F0} ms before reply; "
                    + $"{dispatchTime.TotalMilliseconds:F0} ms dispatch, {(elapsed - dispatchTime).TotalMilliseconds:F0} ms updates."
            );
        }
        using var outputPerformance = SessionPerformance.Begin("output", report);
        // A timer-driven loss can happen while rejecting an unrelated RPC.
        // Its durable capsule transition still has to reach the client.
        if (previous is not null && !context.Selected)
        {
            await output.WriteAsync(new PilotSessionChange(accountId, clientId, null, previous).ToBlue());
            report($"Logged off pilot {previous.PilotId}; returned account {accountId} to character selection.");
        }
        else if (previous is not null && (previous.LocationId != context.Pilot.LocationId || previous.ShipId != context.Pilot.ShipId))
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
            report($"Accepted selection of {pilot.Name} ({pilot.PilotId}) at {pilot.LocationId} in ship {pilot.ShipId}.");
        }

        await output.WriteAsync(packet.CallSucceeded(((PilotCallResult.Success)result).Reply));
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
