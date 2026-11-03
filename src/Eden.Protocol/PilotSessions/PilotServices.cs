using Eden.Gameplay.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Protocol.Skills;
using static Eden.Protocol.PilotSessions.PilotSessionWire;

namespace Eden.Protocol.PilotSessions;

[Injectable(InjectionType.Scoped)]
internal sealed class PilotServices(
    IPilotLookup pilots,
    PilotCallRouter router,
    TimeProvider clock,
    FlightSessionFactory flights,
    PilotUpdateCoordinator updates,
    SkillHandler skills,
    IAsteroidField asteroids
) : IPilotSession
{
    public async Task RunAsync(
        Func<Task<object?>> read,
        Func<object?, Task> write,
        Action<string> report,
        long accountId,
        long clientId,
        SignedSettings signedSettings,
        CancellationToken cancellationToken
    )
    {
        using PilotCallContext context = new(
            accountId,
            pilots.ReadAccount(accountId),
            pilots.ListPilots(accountId),
            flights.Create,
            clientId,
            signedSettings
        );
        PilotSessionOutput output = new(context, accountId, clientId, write, report);
        bool initialStateSent = false;
        Task? checkpoint = null;
        while (true)
        {
            var incoming = read();
            while (
                context.ActiveFlight is { } flight
                && (flight.Moving || flight.InBallpark || context.NextSkillCompletion.HasValue || updates.IsMonitoring)
            )
            {
                checkpoint ??= Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken);
                await Task.WhenAny(incoming, checkpoint);
                if (checkpoint.IsCompleted)
                {
                    await checkpoint;
                    if (context.NextSkillCompletion <= clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
                    {
                        skills.Advance(context);
                        await output.FlushNotificationsAsync();
                    }
                    flight.Checkpoint();
                    var departure = flight.Pilot;
                    if (flight.CompleteJump())
                    {
                        context.ReleaseLocationReferences();
                        await output.WriteAsync(new PilotSessionChange(accountId, clientId, flight.Pilot, departure).ToBlue());
                        report($"Ship {flight.Pilot.ShipId} jumped from {departure.SystemId} to {flight.Pilot.SystemId}.");
                    }
                    if (flight.InBallpark)
                    {
                        flight.RefreshAsteroids(asteroids.List(flight.Pilot.SystemId));
                        flight.RefreshWrecks();
                        flight.RefreshNpcs();
                        flight.RefreshPeers();
                        await output.FlushFlightUpdatesAsync();
                    }
                    var beforeAttack = updates.Advance(context);
                    if (beforeAttack is not null && beforeAttack.ShipId != context.Pilot.ShipId)
                    {
                        await output.AnnounceTransitionAsync(beforeAttack);
                    }
                    await output.FlushFlightUpdatesAsync();
                    await output.FlushNotificationsAsync();
                    checkpoint = null;
                }
                if (incoming.IsCompleted)
                {
                    break;
                }
            }
            var packet = IncomingMachoPacket.Read(await incoming);
            if (packet.Name == PacketNamespace + "PingReq")
            {
                await output.WriteAsync(packet.Ping(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()));
                continue;
            }

            if (packet.Name != PacketNamespace + "CallReq")
            {
                report($"Received {packet.Name}.");
                continue;
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
                continue;
            }
            string service = router.ResolveService(context, packet.Destination(), call.ObjectId);
            string method = call.Method;
            report($"Call {service}.{method}.");
            bool selected = context.Selected;
            DockedPilot? previous = context.ActiveFlight?.Pilot;
            PilotCallResult result = router.Call(context, service, method, call.Arguments, report, call.Keywords);
            if (!selected && context.Selected)
            {
                updates.Enter(context);
            }
            updates.Advance(context);
            // A timer-driven loss can happen while rejecting an unrelated RPC.
            // Its durable capsule transition still has to reach the client.
            if (previous is not null && (previous.LocationId != context.Pilot.LocationId || previous.ShipId != context.Pilot.ShipId))
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
                continue;
            }
            if (result is PilotCallResult.UnsupportedCall)
            {
                await output.WriteAsync(packet.UnsupportedCall(service, method));
                continue;
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
            if (!initialStateSent && service == "machoNet" && method == "GetServiceInfo")
            {
                initialStateSent = true;
                await output.WriteAsync(InitialState(accountId, clientId));
            }
        }
    }
}
