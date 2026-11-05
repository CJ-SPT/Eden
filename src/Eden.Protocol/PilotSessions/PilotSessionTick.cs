using System.Diagnostics;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Protocol.Skills;
using static Eden.Protocol.PilotSessions.PilotSessionWire;

namespace Eden.Protocol.PilotSessions;

internal sealed class PilotSessionTick(
    TimeProvider clock,
    PilotUpdateCoordinator updates,
    SkillHandler skills,
    IAsteroidField asteroids,
    IPilotOperationScope? operations = null
)
{
    private const int SlowUpdateMilliseconds = 250;

    public async Task AdvanceAsync(
        PilotCallContext context,
        PilotSessionOutput output,
        Action<string> report,
        long accountId,
        long clientId
    )
    {
        using var operation = operations?.Begin();
        using var performance = SessionPerformance.Begin("tick", report);
        long started = Stopwatch.GetTimestamp();
        if (!context.Selected)
        {
            updates.Advance(context);
            await output.FlushNotificationsAsync();
            return;
        }
        var flight = context.Flight;
        if (context.NextSkillCompletion <= clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
        {
            skills.Advance(context);
            await output.FlushNotificationsAsync();
        }
        using (SessionPerformance.Begin("motion", report))
        {
            flight.Checkpoint();
        }
        var departure = flight.Pilot;
        if (flight.CompleteTranslocation() is { } moved)
        {
            context.ReleaseLocationReferences();
            context.Roster = context.Roster.Select(p => p.PilotId == moved.Pilot.PilotId ? moved.Pilot : p).ToArray();
            InventoryChangesWire.NotifyItemChange(context, moved.ShipChange);
            updates.Enter(context);
            await output.WriteAsync(new PilotSessionChange(accountId, clientId, flight.Pilot, departure).ToBlue());
            report($"Ship {flight.Pilot.ShipId} translocated from {departure.SystemId} to {flight.Pilot.SystemId}.");
        }
        if (flight.CompleteJump())
        {
            context.ReleaseLocationReferences();
            await output.WriteAsync(new PilotSessionChange(accountId, clientId, flight.Pilot, departure).ToBlue());
            report($"Ship {flight.Pilot.ShipId} jumped from {departure.SystemId} to {flight.Pilot.SystemId}.");
        }
        using (SessionPerformance.Begin("contacts", report))
        {
            if (flight.InBallpark)
            {
                flight.RefreshAsteroids(asteroids.List(flight.Pilot.SystemId));
                flight.RefreshWrecks();
                flight.RefreshMissiles();
                flight.RefreshNpcs();
                flight.RefreshPeers();
                await output.FlushFlightUpdatesAsync();
            }
        }

        DockedPilot? beforeAttack;
        using (SessionPerformance.Begin("updates", report))
        {
            beforeAttack = updates.Advance(context);
        }
        if (
            beforeAttack is not null
            && (
                beforeAttack.ShipId != context.Pilot.ShipId
                || beforeAttack.CorpId != context.Pilot.CorpId
                || beforeAttack.Identity.CorporationRoles != context.Pilot.Identity.CorporationRoles
            )
        )
        {
            await output.AnnounceTransitionAsync(beforeAttack);
        }
        using var outputPerformance = SessionPerformance.Begin("output", report);
        await output.FlushFlightUpdatesAsync();
        await output.FlushNotificationsAsync();
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed.TotalMilliseconds >= SlowUpdateMilliseconds)
        {
            report($"Slow pilot update: {elapsed.TotalMilliseconds:F0} ms.");
        }
    }
}
