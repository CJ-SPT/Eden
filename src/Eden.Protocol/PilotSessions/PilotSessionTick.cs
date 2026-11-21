using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
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
    Eden.Protocol.Mining.AutoMiningHandler? autoMining = null,
    Eden.Protocol.Exploration.AbyssalHandler? abyss = null,
    Eden.Protocol.Exploration.WormholeEnvironmentHandler? environments = null,
    Eden.Gameplay.Sovereignty.IPilotEntosis? entosis = null,
    Eden.Protocol.FactionWarfare.FactionWarzoneUpdates? warzoneUpdates = null
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
        var previousControl = context.ControlledStructureId;
        var beforeConduit = flight.Pilot;
        bool arrived = updates.AdoptConduit(context);
        using var arrivalContext = arrived ? context.Flight.UseSystem() : null;
        if (arrived)
        {
            await output.AnnounceTransitionAsync(beforeConduit, previousControl);
        }
        var beforeControl = context.Pilot;
        var beforePlayerDamage = flight.Pilot;
        using (SessionLatency.Measure(SessionLatencyPhase.PendingDamage))
        {
            updates.ApplyPlayerDamage(context);
        }
        if (beforePlayerDamage.ShipId != context.Pilot.ShipId)
        {
            await output.AnnounceTransitionAsync(beforePlayerDamage, previousControl);
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
            foreach (var change in moved.Changes)
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            updates.Enter(context);
            await output.WriteAsync(new PilotSessionChange(accountId, clientId, flight.Pilot, departure).ToBlue());
            if (moved.Timers is { } timers)
            {
                context.Notifications.Add(
                    new ModuleAttributesChanged([
                        new(
                            moved.Pilot.PilotId,
                            new DogmaItemKey.Item(moved.Pilot.ShipId),
                            DogmaAttributeId.CapacitorCharge,
                            timers.UpdatedAt,
                            moved.Capacitor
                        ),
                    ])
                );
                context.Notifications.Add(new Eden.Protocol.Flight.JumpTimersUpdated(timers));
            }
            string action = moved.Timers is null ? "translocated" : "jumped";
            report($"Ship {flight.Pilot.ShipId} {action} from {departure.SystemId} to {flight.Pilot.SystemId}.");
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
            if (entosis?.Available == true)
            {
                entosis.Advance(context.Pilot.SystemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            }
            using (SessionLatency.Measure(SessionLatencyPhase.Exploration))
            {
                anomalies?.Advance(context);
            }
            warzoneUpdates?.Advance(context);
            abyss?.Advance(context);
            environments?.Advance(context);
            using (SessionLatency.Measure(SessionLatencyPhase.Contacts))
            using (SessionPerformance.Begin("contacts", diagnostics?.Trace ?? report))
            {
                if (structures is not null && (flight.InBallpark || flight.Pilot.InStructure))
                {
                    flight.RefreshStructures(structures.Contacts(flight.Pilot.SystemId));
                }
                if (flight.InBallpark)
                {
                    flight.RefreshCynos();
                    flight.RefreshAsteroids(asteroids.List(flight.Pilot.SystemId));
                    flight.RefreshWrecks();
                    flight.RefreshMissiles();
                    flight.RefreshNpcs();
                    flight.RefreshPeers();
                }
                else if (context.ControlledStructureId is not null)
                {
                    flight.RefreshStructureObservers();
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
        if (previousControl != context.ControlledStructureId)
        {
            await output.AnnounceTransitionAsync(beforeControl, previousControl);
        }
        else if (
            beforeAttack is not null
            && (
                beforeAttack.LocationId != context.Pilot.LocationId
                || beforeAttack.ShipId != context.Pilot.ShipId
                || beforeAttack.CorpId != context.Pilot.CorpId
                || beforeAttack.Identity.AllianceId != context.Pilot.Identity.AllianceId
                || beforeAttack.Identity.WarFaction != context.Pilot.Identity.WarFaction
                || beforeAttack.Identity.Fleet != context.Pilot.Identity.Fleet
                || beforeAttack.Identity.CorporationRoles != context.Pilot.Identity.CorporationRoles
                || beforeAttack.Identity.CorporationAccess != context.Pilot.Identity.CorporationAccess
                || beforeAttack.Identity.CorporationAccountKey != context.Pilot.Identity.CorporationAccountKey
            )
        )
        {
            await output.AnnounceTransitionAsync(beforeAttack, previousControl);
        }
        using var outputLatency = SessionLatency.Measure(SessionLatencyPhase.Output);
        using var outputPerformance = SessionPerformance.Begin("output", diagnostics?.Trace ?? report);
        await output.FlushFlightUpdatesAsync();
        await output.FlushNotificationsAsync();
    }
}
