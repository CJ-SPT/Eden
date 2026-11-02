using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.PilotSessions;

[Injectable(InjectionType.Scoped)]
internal sealed class PilotServices(IPilotLookup pilots, IShipPositionStore positions, PilotCallRouter router, TimeProvider clock, SharedSpace space, ISystemObjects objects, SkillHandler skills) : IPilotSession
{
    private const string PacketNamespace = "carbon.common.script.net.machoNetPacket.";
    private static BlueInstance Address(params object?[] state) => new("carbon.common.script.net.machoNetAddress.MachoAddress", state);

    public async Task RunAsync(Func<Task<object?>> read, Func<object?, Task> write, Action<string> report, long accountId, long clientId, object?[] signedSettings, CancellationToken cancellationToken)
    {
        using PilotCallContext context = new(accountId, pilots.ReadAccount(accountId), pilots.ListPilots(accountId),
            pilot => new FlightSession(positions, pilot, clock, space, objects.Load(pilot.SystemId)), clientId, signedSettings);
        bool initialStateSent = false;
        Task? checkpoint = null;
        while (true)
        {
            var incoming = read();
            while (context.ActiveFlight is { } flight && (flight.Moving || flight.InBallpark || context.NextSkillCompletion.HasValue))
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
                        flight.RefreshPeers();
                        await FlushFlightUpdatesAsync();
                    }
                    checkpoint = null;
                }
                if (incoming.IsCompleted)
                {
                    break;
                }
            }
            if (await incoming is not BlueInstance packet)
            {
                throw new InvalidDataException("Expected a MachoNet packet.");
            }
            object?[] state = PilotLogin.Tuple(packet.State, 14);
            if (packet.Name == PacketNamespace + "PingReq")
            {
                var times = PilotLogin.Tuple(state[4], 1)[0] as List<object?> ?? throw new InvalidDataException("Missing ping times.");
                long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
                foreach (string phase in new[] { "proxy::handle_message", "proxy::writing", "server::turnaround", "proxy::handle_message", "proxy::writing" })
                {
                    times.Add(new object?[] { now, now, phase });
                }
                await write(Response(state, "PingRsp", 21, new object?[] { times }));
                continue;
            }

            if (packet.Name != PacketNamespace + "CallReq")
            {
                report($"Received {packet.Name}.");
                continue;
            }

            object?[] body = PilotLogin.Tuple(state[4], 1);
            object?[] wrapper = PilotLogin.Tuple(body[0], 2);
            byte[] data = wrapper[1] is BlueStream packed ? packed.Bytes : PilotLogin.Bytes(wrapper[1]);
            object?[] call;
            try
            {
                call = PilotLogin.Tuple(BlueMarshal.Decode(data), 4);
            }
            catch (InvalidDataException error)
            {
                report(error.Message);
                // Optional client services send Python containers outside Eden's wire vocabulary.
                // Reject this isolated RPC without dropping the pilot's authenticated flight.
                await write(Response(state, "ErrorResponse", 15, new object?[] { 6, 0, "Unsupported local call arguments" }));
                continue;
            }
            if (state[2] is not BlueInstance destination || destination.State is not object?[] route)
            {
                throw new InvalidDataException("Missing call destination.");
            }

            string service = router.ResolveService(context, route, call[0]);
            string method = BlueMarshal.Text(call[1]);
            report($"Call {service}.{method}.");
            bool selected = context.Selected;
            DockedPilot? previous = context.ActiveFlight?.Pilot;
            object? result = router.Call(context, service, method, call[2], report, call[3] as Dictionary<string, object?>);
            if (result is PilotCallRouter.UserError userError)
            {
                // ErrorResponse params are original command, code and payload. Code 2 wraps an
                // exception stream; inventory catches this UserError to offer a smaller quantity.
                byte[] exception = BlueMarshal.Encode(new BlueConstructor("exceptions.UserError", [userError.Name, userError.Values]));
                await write(Response(state, "ErrorResponse", 15, new object?[] { 6, 2, new object?[] { exception } }));
                continue;
            }
            if (result is PilotCallRouter.Error || ReferenceEquals(result, PilotCallRouter.Unsupported))
            {
                string message = (result as PilotCallRouter.Error)?.Message ?? $"Unsupported local call {service}.{method}";
                await write(Response(state, "ErrorResponse", 15, new object?[] { 6, 0, message }));
                continue;
            }
            if (!selected && context.Selected)
            {
                var pilot = context.Pilot;
                skills.Advance(context);
                await write(SessionChange(accountId, clientId, pilot));
                report($"Accepted selection of {pilot.Name} ({pilot.PilotId}) at {pilot.LocationId} in ship {pilot.ShipId}.");
            }

            if (previous is not null && previous.LocationId != context.Pilot.LocationId)
            {
                await write(SessionChange(accountId, clientId, context.Pilot, previous));
                report($"Ship {context.Pilot.ShipId} moved from {previous.LocationId} to {context.Pilot.LocationId}.");
            }
            await write(Response(state, "CallRsp", 7, new object?[] { BlueMarshal.Encode(result) }));
            if (service == "beyonce" && method == "MachoBindObject" && call[2] is object?[] { Length: 2 } bind && bind[1] is null && context.ActiveFlight?.Pilot.Position is not null)
            {
                context.Flight.Snapshot();
            }
            await FlushFlightUpdatesAsync();
            await FlushNotificationsAsync();
            if (!initialStateSent && service == "machoNet" && method == "GetServiceInfo")
            {
                initialStateSent = true;
                await write(new BlueInstance(PacketNamespace + "SessionInitialStateNotification", new object?[]
                {
                    18, Address(1, PilotLogin.NodeId, null, null), Address(2, clientId, null, null), accountId,
                    new object?[] { clientId, 5, PilotLogin.AccountState(accountId) }, new Dictionary<string, object?>(), null, null, null, null, null, null, null, null,
                }));
            }
        }

        async Task FlushFlightUpdatesAsync()
        {
            if (context.ActiveFlight is { Updates.Count: > 0 } flight)
            {
                await SendNotificationAsync("DoDestinyUpdate", [flight.Updates, false, null]);
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
            // Broadcast -> service -> object-call layers each wrap the payload.
            object?[] update = [1, arguments];
            object?[] payload = [0, BlueMarshal.Encode(new object?[] { 0, update })];
            return write(new BlueInstance(PacketNamespace + "Notification", new object?[]
            {
                12, Address(1, PilotLogin.NodeId, null, null), Address(4, name, new List<object?> { clientId }, "clientID"),
                accountId, new object?[] { payload }, new Dictionary<string, object?>(), null, null, null, null, null, null, null, null,
            }));
        }
    }

    private static Dictionary<string, object?> SessionFields(DockedPilot pilot)
    {
        return new()
        {
            ["charid"] = pilot.PilotId,
            ["corpid"] = pilot.CorpId,
            ["shipid"] = pilot.ShipId,
            ["stationid"] = pilot.Position is null ? pilot.StationId : null,
            ["solarsystemid"] = pilot.Position is null ? null : pilot.SystemId,
            ["stationid2"] = pilot.Position is null ? pilot.StationId : null,
            ["locationid"] = pilot.LocationId,
            ["solarsystemid2"] = pilot.SystemId,
            ["constellationid"] = pilot.ConstellationId,
            ["regionid"] = pilot.RegionId,
            ["raceID"] = pilot.Profile.RaceId,
            ["bloodlineID"] = pilot.Profile.BloodlineId,
            ["genderID"] = pilot.Profile.GenderId,
        };
    }

    private static BlueInstance SessionChange(long accountId, long clientId, DockedPilot pilot, DockedPilot? previous = null)
    {
        var before = previous is null ? new Dictionary<string, object?>() : SessionFields(previous);
        var changes = SessionFields(pilot).Where(field => !Equals(before.GetValueOrDefault(field.Key), field.Value))
            .ToDictionary(field => field.Key, field => (object?)new object?[] { before.GetValueOrDefault(field.Key), field.Value });
        return new(PacketNamespace + "SessionChangeNotification", new object?[]
        {
            16, Address(1, PilotLogin.NodeId, null, null), Address(2, clientId, null, null), accountId,
            new object?[] { clientId, new object?[] { 0, changes }, new List<object?>() },
            new Dictionary<string, object?>(), null, null, null, null, null, null, null, null,
        });
    }

    private static BlueInstance Response(object?[] state, string name, int command, object? body) => new(PacketNamespace + name,
        new object?[] { command, state[2], state[1], state[3], body, new Dictionary<string, object?>(), null, null, state[8], null, state[10], state[11], state[12], null });
}
