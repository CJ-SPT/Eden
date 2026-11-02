using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal static class PilotServices
{
    private const string PacketNamespace = "carbon.common.script.net.machoNetPacket.";
    private static BlueInstance Address(params object?[] state) => new("carbon.common.script.net.machoNetAddress.MachoAddress", state);

    public static async Task RunAsync(Func<Task<object?>> read, Func<object?, Task> write, Action<string> report, long clientId, IDockedWorld world, object?[] signedSettings)
    {
        DockedPilot pilot = world.FindPilot(PilotLogin.UserId) ?? throw new InvalidDataException("Account has no active pilot.");
        FlightSession flight = new(world, pilot, TimeProvider.System);
        DockedServices? docked = null;
        bool initialStateSent = false;
        Task? checkpoint = null;
        while (true)
        {
            var incoming = read();
            while (flight.Moving)
            {
                checkpoint ??= Task.Delay(TimeSpan.FromSeconds(1));
                await Task.WhenAny(incoming, checkpoint);
                if (checkpoint.IsCompleted)
                {
                    flight.Checkpoint();
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
                long now = DateTime.UtcNow.ToFileTimeUtc();
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
            catch (InvalidDataException)
            {
                // Optional client services send Python containers outside Eden's wire vocabulary.
                // Reject this isolated RPC without dropping the pilot's authenticated flight.
                await write(Response(state, "ErrorResponse", 15, new object?[] { 6, 0, "Unsupported local call arguments" }));
                continue;
            }
            if (state[2] is not BlueInstance destination || destination.State is not object?[] route)
            {
                throw new InvalidDataException("Missing call destination.");
            }

            string service = route switch
            {
                [1L, (long)PilotLogin.NodeId, null, _] => docked?.ResolveReference(call[0]) ?? "unknown binding",
                [1L, _, null, _] => "unknown binding",
                [1L, _, var name, _] => BlueMarshal.Text(name),
                [8L, var name, _] => BlueMarshal.Text(name),
                _ => throw new InvalidDataException("Invalid service address."),
            };
            if (service.StartsWith('@') && route is not [1L, (long)PilotLogin.NodeId, null, _])
            {
                service = "unknown binding";
            }
            string method = BlueMarshal.Text(call[1]);
            report($"Call {service}.{method}.");
            if (service == "charUnboundMgr" && method == "SelectCharacterID")
            {
                if (call[2] is not object?[] { Length: > 0 } args || !Equals(args[0], pilot.PilotId))
                {
                    await write(Response(state, "ErrorResponse", 15, new object?[] { 6, 0, "Unknown pilot for this account" }));
                    continue;
                }

                if (docked is null)
                {
                    await write(SessionChange(clientId, pilot));
                    docked = new(pilot, clientId, flight);
                }
                await write(Response(state, "CallRsp", 7, new object?[] { BlueMarshal.Encode(null) }));
                report($"Accepted selection of {pilot.Name} ({pilot.PilotId}) at {pilot.LocationId} in ship {pilot.ShipId}.");
                continue;
            }

            DockedPilot previous = flight.Pilot;
            object? result = (service, method) switch
            {
                ("machoNet", "GetServiceInfo") => new Dictionary<string, object?>(),
                ("machoNet", "GetTime") => DateTime.UtcNow.ToFileTimeUtc(),
                ("charUnboundMgr", "GetCharacterSelectionData") => CharacterSelection(pilot),
                ("config", "GetMultiOwnersEx") => OwnerRows(call[2], pilot),
                ("config", "GetMultiCorpTickerNamesEx") => new object?[] { new object?[] { "corporationID", "tickerName" }, new List<object?> { new object?[] { 1000044, "SWA" } } },
                ("config", "GetBlackListedPlanets" or "GetOldStationData") => new List<object?>(),
                ("config", "GetAverageMarketPrices") => new Dictionary<long, object?>(),
                ("invbroker", "GetItemDescriptor") => DockedServices.ItemDescriptor(),
                ("air_npe", "is_air_npe_enabled") => false,
                ("subscriptionMgr", "GetCloneGrade") => 0,
                ("map", "GetSecurityModifiedSystems") => new BlueInstance("eve.common.script.sys.rowset.Rowset",
                    new Dictionary<string, object?> { ["header"] = new List<object?> { "solarSystemID", "security", "modifiedSecurity" }, ["lines"] = new List<object?>() }),
                ("userSvc", "GetRedeemTokens") => new List<object?>(),
                ("seasonalLoginCampaignManager", "get_active_campaign") => new object?[] { null, null, null, 0 },
                ("alert", "BeanCount") => new object?[] { 1, 0 },
                ("charUnboundMgr", "GetCharOmegaDowngradeStatus") => new Dictionary<string, object?>(),
                ("loginCampaignManager", "get_client_campaign_state") => null,
                ("charMgr", "GetSettingsInfo") when docked is not null => signedSettings,
                ("multiLoginBlocker", "Login") => true,
                ("charUnboundMgr", "GetCharacterLockType") => null,
                ("populationCap", "MachoResolveObject") => PilotLogin.NodeId,
                ("populationCap", "MachoBindObject") when IsPilotLoadSlot(call[2], pilot.PilotId) => new object?[] { new BlueObjectReference($"N={PilotLogin.NodeId}:1", 1), new Dictionary<string, object?>() },
                _ => docked is null || call[2] is not object?[] arguments ? DockedServices.Unsupported : docked.Call(service, method, arguments, report),
            };
            if (ReferenceEquals(result, DockedServices.Unsupported))
            {
                await write(Response(state, "ErrorResponse", 15, new object?[] { 6, 0, $"Unsupported local call {service}.{method}" }));
                continue;
            }

            if (previous.LocationId != flight.Pilot.LocationId)
            {
                await write(SessionChange(clientId, flight.Pilot, previous));
                report($"Ship {pilot.ShipId} moved from {previous.LocationId} to {flight.Pilot.LocationId}.");
            }
            await write(Response(state, "CallRsp", 7, new object?[] { BlueMarshal.Encode(result) }));
            if (service == "beyonce" && method == "MachoBindObject" && call[2] is object?[] { Length: 2 } bind && bind[1] is null && flight.Pilot.Position is not null)
            {
                flight.Snapshot();
            }
            if (flight.Updates.Count > 0)
            {
                // Broadcast -> service -> object-call layers each wrap the payload.
                object?[] update = [1, new object?[] { flight.Updates, false, null }];
                object?[] payload = [0, BlueMarshal.Encode(new object?[] { 0, update })];
                await write(new BlueInstance(PacketNamespace + "Notification", new object?[]
                {
                    12, Address(1, PilotLogin.NodeId, null, null), Address(4, "DoDestinyUpdate", new List<object?> { clientId }, "clientID"),
                    PilotLogin.UserId, new object?[] { payload }, new Dictionary<string, object?>(), null, null, null, null, null, null, null, null,
                }));
                flight.Updates.Clear();
            }
            if (!initialStateSent && service == "machoNet" && method == "GetServiceInfo")
            {
                initialStateSent = true;
                await write(new BlueInstance(PacketNamespace + "SessionInitialStateNotification", new object?[]
                {
                    18, Address(1, PilotLogin.NodeId, null, null), Address(2, clientId, null, null), PilotLogin.UserId,
                    new object?[] { clientId, 5, PilotLogin.AccountState() }, new Dictionary<string, object?>(), null, null, null, null, null, null, null, null,
                }));
            }
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
            ["raceID"] = 1,
            ["bloodlineID"] = 1,
            ["genderID"] = 1,
        };
    }

    private static BlueInstance SessionChange(long clientId, DockedPilot pilot, DockedPilot? previous = null)
    {
        var before = previous is null ? new Dictionary<string, object?>() : SessionFields(previous);
        var changes = SessionFields(pilot).Where(field => !Equals(before.GetValueOrDefault(field.Key), field.Value))
            .ToDictionary(field => field.Key, field => (object?)new object?[] { before.GetValueOrDefault(field.Key), field.Value });
        return new(PacketNamespace + "SessionChangeNotification", new object?[]
        {
            16, Address(1, PilotLogin.NodeId, null, null), Address(2, clientId, null, null), PilotLogin.UserId,
            new object?[] { clientId, new object?[] { 0, changes }, new List<object?>() },
            new Dictionary<string, object?>(), null, null, null, null, null, null, null, null,
        });
    }

    private static bool IsPilotLoadSlot(object? value, long pilotId) =>
        value is object?[] { Length: 2 } args && args[0] is object?[] { Length: 2 } bind && Equals(bind[0], pilotId) &&
        args[1] is object?[] { Length: > 1 } call && BlueMarshal.Text(call[0]) == "GetCharacterLoadSlot" &&
        call[1] is object?[] { Length: 1 } ids && Equals(ids[0], pilotId);

    private static BlueInstance Response(object?[] state, string name, int command, object? body) => new(PacketNamespace + name,
        new object?[] { command, state[2], state[1], state[3], body, new Dictionary<string, object?>(), null, null, state[8], null, state[10], state[11], state[12], null });

    private static object?[] OwnerRows(object? arguments, DockedPilot pilot)
    {
        var ids = PilotLogin.Tuple(arguments, 1)[0] as List<object?> ?? throw new InvalidDataException("Expected owner IDs.");
        List<object?> rows = [];
        foreach (object? id in ids)
        {
            if (Equals(id, pilot.PilotId))
            {
                rows.Add(new object?[] { pilot.PilotId, pilot.Name, 1373, 1, null });
            }
        }

        return [new object?[] { "ownerID", "ownerName", "typeID", "gender", "ownerNameID" }, rows];
    }

    private static object?[] CharacterSelection(DockedPilot docked)
    {
        BlueInstance account = new("utillib.KeyVal", new Dictionary<string, object?>
        {
            ["userName"] = "Eden",
            ["characterSlots"] = 1,
            ["maxCharacterSlots"] = 1,
            ["creationDate"] = 134000000000000000L,
            ["subscriptionEndTime"] = null,
        });
        BlueInstance pilot = new("utillib.KeyVal", new Dictionary<string, object?>
        {
            ["characterID"] = docked.PilotId,
            ["characterName"] = docked.Name,
            ["name"] = docked.Name,
            ["stationID"] = docked.Position is null ? docked.StationId : null,
            ["solarSystemID"] = docked.SystemId,
            ["shipTypeID"] = docked.ShipTypeId,
            ["corporationID"] = docked.CorpId,
            ["allianceID"] = null,
            ["factionID"] = null,
            ["balance"] = 0,
            ["balanceChange"] = 0,
            ["skillPoints"] = 0,
            ["finishedSkills"] = 0,
            ["unreadMailCount"] = 0,
            ["paperdollState"] = 0,
            ["lockTypeID"] = null,
            ["deletePrepareDateTime"] = null,
            ["logoffDate"] = 134000000000000000L,
            ["skillTypeID"] = null,
            ["gender"] = 1,
            ["bloodlineID"] = 1,
            ["raceID"] = 1,
        });
        return [new List<object?> { account }, new object?[] { null, null }, new List<object?> { pilot }, new List<object?>()];
    }
}
