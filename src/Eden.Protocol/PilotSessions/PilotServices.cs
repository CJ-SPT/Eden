using System.Diagnostics;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Chat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Skills;
using Eden.Protocol.Trading;

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
    StationGuestHandler guests,
    Eden.Protocol.Exploration.CombatAnomalyHandler anomalies,
    PlayerTradeHandler trades,
    IPilotOperationScope? operations = null,
    ChatHandler? chat = null,
    SessionDiagnostics? diagnostics = null,
    ICapsuleerHandoff? handoff = null,
    ICapsuleerPresentation? presentation = null,
    Eden.Modding.IModSessionEvents? modEvents = null,
    Eden.Protocol.Mining.AutoMiningHandler? autoMining = null,
    Eden.Protocol.Exploration.AbyssalHandler? abyss = null,
    Eden.Protocol.Exploration.WormholeEnvironmentHandler? environments = null,
    SessionFrameMonitor? frames = null,
    Eden.Gameplay.FactionWarfare.IFactionWarzones? warzones = null,
    ExpertSystemHandler? expertSystems = null,
    Eden.Protocol.Exploration.IncursionAgencyMonitor? incursionAgency = null
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
        using var tradePresence = trades;
        using var chatPresence = chat;
        using var context = CreateContext();
        using var modSession = new Eden.Protocol.Modding.ModSessionObserver(context, modEvents, operations);

        PilotCallContext CreateContext()
        {
            using var operation = operations?.Begin();
            return new(accountId, pilots.ReadAccount(accountId), pilots.ListPilots(accountId), flights.Create, clientId, signedSettings)
            {
                Roles = identity.ReadRoles(accountId),
            };
        }

        PilotSessionOutput output = new(context, accountId, clientId, write, report, diagnostics);
        PilotSessionCalls calls = new(
            context,
            output,
            router,
            updates,
            skills,
            clock,
            report,
            accountId,
            clientId,
            operations,
            diagnostics,
            handoff,
            presentation,
            expertSystems
        );
        PilotSessionTick tick = new(
            clock,
            updates,
            skills,
            asteroids,
            operations,
            diagnostics,
            anomalies,
            handoff,
            presentation,
            autoMining: autoMining,
            abyss: abyss,
            environments: environments,
            warzoneUpdates: warzones is null ? null : new Eden.Protocol.FactionWarfare.FactionWarzoneUpdates(warzones),
            expertSystems: expertSystems,
            incursionAgency: incursionAgency
        );
        async Task<(object? Packet, long Received)> ReadArrivalAsync()
        {
            var packet = await read();
            return (packet, Stopwatch.GetTimestamp());
        }
        // A rental keeps an otherwise idle pilot's session asleep until its deadline.
        async Task WaitForExpertSystemExpiryAsync(Task incoming)
        {
            while (expertSystems is not null && !incoming.IsCompleted && context.NextExpertSystemExpiry is long deadline)
            {
                long remaining = deadline - clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
                if (remaining > 0)
                {
                    using var stopWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    // Split long waits within the timer's supported range.
                    var delay = TimeSpan.FromTicks(Math.Clamp(remaining, TimeSpan.TicksPerMillisecond, TimeSpan.FromDays(30).Ticks));
                    var deadlineWait = Task.Delay(delay, clock, stopWait.Token);
                    if (await Task.WhenAny(incoming, deadlineWait) == incoming)
                    {
                        await stopWait.CancelAsync();
                        return;
                    }
                    await deadlineWait;
                    if (deadline > clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
                    {
                        continue;
                    }
                }

                await tick.AdvanceAsync(context, output, report, accountId, clientId);
                modSession.Observe();
            }
        }

        Task? checkpoint = null;
        SessionTickSchedule? schedule = null;
        try
        {
            while (true)
            {
                var incoming = ReadArrivalAsync();
                // Account rewards can change while the native character selector is open.
                bool ticking;
                while (
                    ticking =
                        updates.IsAccountMonitoring
                        || (
                            context.ActiveFlight is { } flight
                            && (flight.Moving || flight.InBallpark || context.NextSkillCompletion.HasValue || updates.IsMonitoring)
                        )
                )
                {
                    schedule ??= new SessionTickSchedule(clock);
                    schedule.ResumeMeasurement();
                    checkpoint ??= Task.Delay(schedule.Remaining, clock, cancellationToken);
                    await Task.WhenAny(incoming, checkpoint);
                    if (checkpoint.IsCompleted)
                    {
                        await checkpoint;
                        bool measure = frames is not null && context.Selected;
                        long started = measure ? clock.GetTimestamp() : 0;
                        var startDelay = measure ? schedule.StartDelay(started) : TimeSpan.Zero;
                        await tick.AdvanceAsync(context, output, report, accountId, clientId);
                        modSession.Observe();
                        var elapsed = measure ? clock.GetElapsedTime(started) : TimeSpan.Zero;
                        schedule.Complete();
                        if (measure)
                        {
                            frames!.Record(elapsed, startDelay);
                        }
                        checkpoint = null;
                    }
                    if (incoming.IsCompleted)
                    {
                        break;
                    }
                }
                if (!ticking)
                {
                    schedule?.PauseMeasurement();
                    await WaitForExpertSystemExpiryAsync(incoming);
                }
                var arrived = await incoming;
                SessionPerformance.ReportQueue(arrived.Received, diagnostics?.Trace ?? report);
                await calls.ProcessAsync(arrived.Packet, arrived.Received);
                modSession.Observe();
            }
        }
        finally
        {
            if (context.Selected)
            {
                using var operation = operations?.Begin();
                using var system = context.Flight.UseSystem();
                updates.DisconnectAutoMining(context);
            }
        }
    }
}
