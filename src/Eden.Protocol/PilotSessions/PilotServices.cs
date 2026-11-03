using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
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
        PilotSessionCalls calls = new(context, output, router, updates, skills, clock, report, accountId, clientId);
        PilotSessionTick tick = new(clock, updates, skills, asteroids);
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
                    await tick.AdvanceAsync(context, output, report, accountId, clientId);
                    checkpoint = null;
                }
                if (incoming.IsCompleted)
                {
                    break;
                }
            }
            await calls.ProcessAsync(await incoming);
        }
    }
}
