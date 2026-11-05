using System.Diagnostics;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Protocol.Skills;

namespace Eden.Protocol.PilotSessions;

[Injectable(InjectionType.Scoped)]
internal sealed class PilotServices(
    IPilotLookup pilots,
    PilotCallRouter router,
    TimeProvider clock,
    FlightSessionFactory flights,
    PilotUpdateCoordinator updates,
    SkillHandler skills,
    IAsteroidField asteroids,
    ILoginIdentity identity,
    StationGuestHandler guests
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
        // Release station presence even when a read, write or cancellation ends
        // the session before the connection's dependency scope is disposed.
        using var stationPresence = guests;
        using PilotCallContext context = new(
            accountId,
            pilots.ReadAccount(accountId),
            pilots.ListPilots(accountId),
            flights.Create,
            clientId,
            signedSettings
        )
        {
            Roles = identity.ReadRoles(accountId),
        };
        PilotSessionOutput output = new(context, accountId, clientId, write, report);
        PilotSessionCalls calls = new(context, output, router, updates, skills, clock, report, accountId, clientId);
        PilotSessionTick tick = new(clock, updates, skills, asteroids);
        async Task<(object? Packet, long Received)> ReadArrivalAsync()
        {
            var packet = await read();
            return (packet, Stopwatch.GetTimestamp());
        }
        Task? checkpoint = null;
        SessionTickSchedule? schedule = null;
        while (true)
        {
            var incoming = ReadArrivalAsync();
            // Account rewards can change while the native character selector is open.
            while (
                updates.IsAccountMonitoring
                || (
                    context.ActiveFlight is { } flight
                    && (flight.Moving || flight.InBallpark || context.NextSkillCompletion.HasValue || updates.IsMonitoring)
                )
            )
            {
                schedule ??= new SessionTickSchedule(clock);
                checkpoint ??= Task.Delay(schedule.Remaining, clock, cancellationToken);
                await Task.WhenAny(incoming, checkpoint);
                if (checkpoint.IsCompleted)
                {
                    await checkpoint;
                    await tick.AdvanceAsync(context, output, report, accountId, clientId);
                    schedule.Complete();
                    checkpoint = null;
                }
                if (incoming.IsCompleted)
                {
                    break;
                }
            }
            var arrived = await incoming;
            SessionPerformance.ReportQueue(arrived.Received, report);
            await calls.ProcessAsync(arrived.Packet);
        }
    }
}
