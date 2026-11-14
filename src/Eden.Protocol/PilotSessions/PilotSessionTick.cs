using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Docking;
using Eden.Protocol.Skills;

namespace Eden.Protocol.PilotSessions;

internal sealed class PilotSessionTick(
    TimeProvider clock,
    PilotUpdateCoordinator updates,
    SkillHandler skills,
    IAsteroidField asteroids,
    IPilotOperationScope? operations = null,
    SessionDiagnostics? diagnostics = null,
    Eden.Protocol.Exploration.CombatAnomalyHandler? anomalies = null,
    ICapsuleerHandoff? handoff = null,
    ICapsuleerPresentation? presentation = null,
    Eden.Gameplay.Structures.IStructureContacts? structures = null,
    Eden.Protocol.Mining.AutoMiningHandler? autoMining = null
)
{
    public async Task AdvanceAsync(
        PilotCallContext context,
        PilotSessionOutput output,
        Action<string> report,
        long accountId,
        long clientId
    )
    {
        using var operation = operations?.Begin();
        using var systemContext = context.Selected ? context.Flight.UseSystem() : null;
        using var performance = SessionPerformance.Begin("tick", diagnostics?.Trace ?? report);
        using var latency = new SessionLatency("tick", diagnostics?.Warning ?? report, diagnostics?.Trace ?? report);
        if (handoff is not null && context.Selected)
        {
            await handoff.SynchronizeAsync(
                context.Pilot.SystemId,
                () =>
                {
                    using var refresh = SessionLatency.Measure(SessionLatencyPhase.TrafficRefresh);
                    presentation?.Refresh();
                }
            );
        }
        if (!context.Selected)
        {
            updates.Advance(context);
            await output.FlushNotificationsAsync();
            return;
        }
        var flight = context.Flight;
        var beforePlayerDamage = flight.Pilot;
        using (SessionLatency.Measure(SessionLatencyPhase.PendingDamage))
        {
            updates.ApplyPlayerDamage(context);
        }
        if (beforePlayerDamage.ShipId != context.Pilot.ShipId)
        {
            await output.AnnounceTransitionAsync(beforePlayerDamage);
        }
        if (context.NextSkillCompletion <= clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
        {
            skills.Advance(context);
            await output.FlushNotificationsAsync();
        }
        using (SessionLatency.Measure(SessionLatencyPhase.Motion))
        using (SessionPerformance.Begin("motion", diagnostics?.Trace ?? report))
        {
            flight.Checkpoint();
        }
        flight.StartPendingTranslocation();
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
        if (flight.ConsumeWormholeJumpCancellation())
        {
            context.Notifications.Add(new Eden.Protocol.Exploration.WormholeJumpCancelled());
        }
        DockedPilot? beforeAttack = null;
        using var destinationContext = context.Flight.UseSystem();
        void PrepareUpdates()
        {
            anomalies?.Advance(context);
            using (SessionLatency.Measure(SessionLatencyPhase.Contacts))
            using (SessionPerformance.Begin("contacts", diagnostics?.Trace ?? report))
            {
                if (flight.InBallpark)
                {
                    if (structures is not null)
                    {
                        flight.RefreshStructures(structures.Contacts(flight.Pilot.SystemId));
                    }
                    flight.RefreshAsteroids(asteroids.List(flight.Pilot.SystemId));
                    flight.RefreshWrecks();
                    flight.RefreshMissiles();
                    flight.RefreshNpcs();
                    flight.RefreshPeers();
                }
            }

            using (SessionLatency.Measure(SessionLatencyPhase.Updates))
            using (SessionPerformance.Begin("updates", diagnostics?.Trace ?? report))
            {
                beforeAttack = updates.Advance(context);
                autoMining?.Advance(context, sessionTick: true);
            }
        }
        if (handoff is not null)
        {
            // A frame's synchronous observer work takes one place in the ownership queue.
            // Network output remains after release so a slow socket cannot retain ownership.
            await handoff.SynchronizeAsync(context.Pilot.SystemId, PrepareUpdates);
        }
        else
        {
            PrepareUpdates();
        }
        if (
            beforeAttack is not null
            && (
                beforeAttack.LocationId != context.Pilot.LocationId
                || beforeAttack.ShipId != context.Pilot.ShipId
                || beforeAttack.CorpId != context.Pilot.CorpId
                || beforeAttack.Identity.Fleet != context.Pilot.Identity.Fleet
                || beforeAttack.Identity.CorporationRoles != context.Pilot.Identity.CorporationRoles
                || beforeAttack.Identity.CorporationAccess != context.Pilot.Identity.CorporationAccess
                || beforeAttack.Identity.CorporationAccountKey != context.Pilot.Identity.CorporationAccountKey
            )
        )
        {
            await output.AnnounceTransitionAsync(beforeAttack);
        }
        using var outputLatency = SessionLatency.Measure(SessionLatencyPhase.Output);
        using var outputPerformance = SessionPerformance.Begin("output", diagnostics?.Trace ?? report);
        await output.FlushFlightUpdatesAsync();
        await output.FlushNotificationsAsync();
    }
}
