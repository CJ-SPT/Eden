using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Industry;
using Eden.Protocol.Loot;
using Eden.Protocol.Mining;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using static Eden.Protocol.PilotSessions.PilotSessionWire;

namespace Eden.Protocol.PilotSessions;

[Injectable(InjectionType.Scoped)]
internal sealed class PilotServices(
    IPilotLookup pilots,
    IShipPositionStore positions,
    PilotCallRouter router,
    TimeProvider clock,
    SharedSpace space,
    ISystemObjects objects,
    SkillHandler skills,
    TargetingHandler targeting,
    IAsteroidField asteroids,
    MiningHandler mining,
    IPilotInventory inventory,
    IStationDocking docking,
    IPilotHealth health,
    INpcEncounters encounters,
    CombatHandler combat,
    NpcCombatHandler npcCombat,
    IWrecks wrecks,
    ShieldBoostHandler shields,
    IndustryHandler industry,
    PropulsionHandler propulsion
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
            pilot => new FlightSession(
                positions,
                pilot,
                clock,
                space,
                objects.Load(pilot.SystemId),
                inventory
                    .List(pilot.PilotId, pilot.ShipId)
                    .Where(i => InventoryDefinitions.IsHighSlot(i.FlagId))
                    .Select(i => new FittedSpaceModule(i.Id, i.TypeId, i.FlagId))
                    .ToArray(),
                docking,
                health,
                encounters,
                wrecks
            ),
            clientId,
            (object?[])signedSettings.ToBlue()
        );
        bool initialStateSent = false;
        Task? checkpoint = null;
        while (true)
        {
            var incoming = read();
            while (
                context.ActiveFlight is { } flight
                && (flight.Moving || flight.InBallpark || context.NextSkillCompletion.HasValue || industry.IsMonitoring)
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
                        await FlushNotificationsAsync();
                    }
                    flight.Checkpoint();
                    var departure = flight.Pilot;
                    if (flight.CompleteJump())
                    {
                        context.ReleaseLocationReferences();
                        await write(SessionChange(accountId, clientId, flight.Pilot, departure));
                        report($"Ship {flight.Pilot.ShipId} jumped from {departure.SystemId} to {flight.Pilot.SystemId}.");
                    }
                    if (flight.InBallpark)
                    {
                        flight.RefreshAsteroids(asteroids.List(flight.Pilot.SystemId));
                        flight.RefreshWrecks();
                        flight.RefreshNpcs();
                        flight.RefreshPeers();
                        await FlushFlightUpdatesAsync();
                    }
                    targeting.Advance(context);
                    mining.Advance(context);
                    combat.Advance(context);
                    shields.Advance(context);
                    propulsion.Advance(context);
                    industry.Advance(context);
                    var beforeAttack = context.Pilot;
                    npcCombat.Advance(context);
                    targeting.Advance(context);
                    if (beforeAttack.ShipId != context.Pilot.ShipId)
                    {
                        await AnnounceTransitionAsync(beforeAttack);
                    }
                    await FlushFlightUpdatesAsync();
                    await FlushNotificationsAsync();
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
                await write(packet.Ping(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()));
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
                await write(packet.Response("ErrorResponse", 15, new object?[] { 6, 0, "Unsupported local call arguments" }));
                continue;
            }
            string service = router.ResolveService(context, packet.Destination(), call.ObjectId);
            string method = call.Method;
            report($"Call {service}.{method}.");
            bool selected = context.Selected;
            DockedPilot? previous = context.ActiveFlight?.Pilot;
            PilotCallResult result = router.Dispatch(context, service, method, call.Arguments, report, call.Keywords);
            if (!selected && context.Selected)
            {
                npcCombat.Enter(context);
            }
            targeting.Advance(context);
            mining.Advance(context);
            combat.Advance(context);
            shields.Advance(context);
            propulsion.Advance(context);
            industry.Advance(context);
            npcCombat.Advance(context);
            targeting.Advance(context);
            // A timer-driven loss can happen while rejecting an unrelated RPC.
            // Its durable capsule transition still has to reach the client.
            if (previous is not null && (previous.LocationId != context.Pilot.LocationId || previous.ShipId != context.Pilot.ShipId))
            {
                await AnnounceTransitionAsync(previous);
            }
            if (result is PilotCallResult.Failure validationError)
            {
                // The pinned fitting service uses CustomInfo/info for a readable
                // gameplay rejection. Macho error code 0 means a bad destination.
                result = new PilotCallResult.NamedFailure(
                    "CustomInfo",
                    new PilotCallResult.LegacyReply(new Dictionary<string, object?> { ["info"] = validationError.Message })
                );
            }
            if (result is PilotCallResult.NamedFailure userError)
            {
                // ErrorResponse params are original command, code and payload. Code 2 wraps an
                // exception stream. Named errors retain their client-specific handling,
                // including inventory's smaller-quantity retry for cargo capacity.
                byte[] exception = BlueMarshal.Encode(new BlueConstructor("exceptions.UserError", [userError.Name, userError.Values]));
                await write(packet.Response("ErrorResponse", 15, new object?[] { 6, 2, new object?[] { exception } }));
                continue;
            }
            if (ReferenceEquals(result, PilotCallRouter.Unsupported))
            {
                string message = $"Unsupported local call {service}.{method}";
                await write(packet.Response("ErrorResponse", 15, new object?[] { 6, 0, message }));
                continue;
            }
            if (!selected && context.Selected)
            {
                var pilot = context.Pilot;
                skills.Advance(context);
                await write(SessionChange(accountId, clientId, pilot));
                report($"Accepted selection of {pilot.Name} ({pilot.PilotId}) at {pilot.LocationId} in ship {pilot.ShipId}.");
            }

            await write(packet.Response("CallRsp", 7, new object?[] { BlueMarshal.Encode(((PilotCallResult.Success)result).Reply) }));
            if (
                service == "beyonce"
                && method == "MachoBindObject"
                && call.IsBindingWithoutCall
                && context.ActiveFlight?.Pilot.Position is not null
            )
            {
                context.Flight.Snapshot();
            }
            await FlushFlightUpdatesAsync();
            await FlushNotificationsAsync();
            if (!initialStateSent && service == "machoNet" && method == "GetServiceInfo")
            {
                initialStateSent = true;
                await write(InitialState(accountId, clientId));
            }
        }

        async Task AnnounceTransitionAsync(DockedPilot previous)
        {
            if (previous.ShipId != context.Pilot.ShipId)
            {
                // HangarView needs new inventory rows before its ship delta. In
                // space, load the capsule ball before the client adopts its ego.
                await FlushNotificationsAsync();
                if (previous.Position is not null)
                {
                    await FlushFlightUpdatesAsync();
                }
            }
            if (previous.LocationId != context.Pilot.LocationId)
            {
                context.ReleaseLocationReferences();
            }
            await write(SessionChange(accountId, clientId, context.Pilot, previous));
            report(
                previous.ShipId != context.Pilot.ShipId
                    ? $"Pilot {context.Pilot.PilotId} boarded ship {context.Pilot.ShipId} "
                        + $"from {previous.ShipId} at {context.Pilot.LocationId}."
                    : $"Ship {context.Pilot.ShipId} moved from {previous.LocationId} to {context.Pilot.LocationId}."
            );
        }

        async Task FlushFlightUpdatesAsync()
        {
            foreach (var (name, arguments) in context.NotificationsBeforeFlight)
            {
                await SendNotificationAsync(name, arguments);
            }
            context.NotificationsBeforeFlight.Clear();
            if (context.ActiveFlight is { Updates.Count: > 0 } flight)
            {
                await SendNotificationAsync("DoDestinyUpdate", new DestinyUpdatesNotification(flight.Updates).Arguments);
                flight.Updates.Clear();
            }
        }

        async Task FlushNotificationsAsync()
        {
            foreach (var (name, arguments) in context.Notifications)
            {
                await SendNotificationAsync(name, arguments);
            }
            context.Notifications.Clear();
        }

        Task SendNotificationAsync(string name, object?[] arguments)
        {
            report($"Notify {name}.");
            return write(Notification(accountId, clientId, name, arguments));
        }
    }
}
