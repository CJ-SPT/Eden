namespace Eden.Protocol;

internal static class PilotServices
{
    private const string PacketNamespace = "carbon.common.script.net.machoNetPacket.";
    private static BlueInstance Address(params object?[] state) => new("carbon.common.script.net.machoNetAddress.MachoAddress", state);

    public static async Task RunAsync(Func<Task<object?>> read, Func<object?, Task> write, Action<string> report, long clientId)
    {
        int? selectedPilot = null;
        bool initialStateSent = false;
        while (true)
        {
            if (await read() is not BlueInstance packet)
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
            object?[] call = PilotLogin.Tuple(BlueMarshal.Decode(data), 4);
            if (state[2] is not BlueInstance destination || destination.State is not object?[] route)
            {
                throw new InvalidDataException("Missing call destination.");
            }

            string service = route switch
            {
                [1L, _, var name, _] => BlueMarshal.Text(name),
                [8L, var name, _] => BlueMarshal.Text(name),
                _ => throw new InvalidDataException("Invalid service address."),
            };
            string method = BlueMarshal.Text(call[1]);
            report($"Call {service}.{method}.");
            if (service == "charUnboundMgr" && method == "SelectCharacterID")
            {
                if (call[2] is not object?[] { Length: > 0 } args || args[0] is not (long)PilotLogin.PilotId)
                {
                    await write(Response(state, "ErrorResponse", 15, new object?[] { 6, 0, "Unknown pilot for this account" }));
                    continue;
                }

                selectedPilot ??= PilotLogin.PilotId;
                await write(Response(state, "CallRsp", 7, new object?[] { BlueMarshal.Encode(null) }));
                report($"Accepted selection of Eden Pilot ({selectedPilot}) for this session.");
                continue;
            }

            object? result = (service, method) switch
            {
                ("machoNet", "GetServiceInfo") => new Dictionary<string, object?>(),
                ("machoNet", "GetTime") => DateTime.UtcNow.ToFileTimeUtc(),
                ("charUnboundMgr", "GetCharacterSelectionData") => CharacterSelection(),
                ("config", "GetMultiOwnersEx") => OwnerRows(call[2]),
                ("config", "GetMultiCorpTickerNamesEx") => new object?[] { new object?[] { "corporationID", "tickerName" }, new List<object?> { new object?[] { 1000044, "SWA" } } },
                ("config", "GetBlackListedPlanets" or "GetOldStationData" or "GetAverageMarketPrices") => new List<object?>(),
                ("invbroker", "GetItemDescriptor") => new List<object?>
                {
                    new object?[] { "itemID", 20, 8 },
                    new object?[] { "typeID", 3, 4 },
                    new object?[] { "locationID", 20, 8 },
                    new object?[] { "ownerID", 3, 4 },
                    new object?[] { "flagID", 2, 2 },
                    new object?[] { "groupID", 3, 4 },
                    new object?[] { "categoryID", 3, 4 },
                },
                ("air_npe", "is_air_npe_enabled") => false,
                ("subscriptionMgr", "GetCloneGrade") => 0,
                ("map", "GetSecurityModifiedSystems") => new BlueInstance("eve.common.script.sys.rowset.Rowset",
                    new Dictionary<string, object?> { ["header"] = new List<object?> { "solarSystemID", "security", "modifiedSecurity" }, ["lines"] = new List<object?>() }),
                ("userSvc", "GetRedeemTokens") => new List<object?>(),
                ("seasonalLoginCampaignManager", "get_active_campaign") => new object?[] { null, null, null, 0 },
                ("alert", "BeanCount") => new object?[] { 1, 0 },
                ("charUnboundMgr", "GetCharOmegaDowngradeStatus") => new Dictionary<string, object?>(),
                ("loginCampaignManager", "get_client_campaign_state") => null,
                ("multiLoginBlocker", "Login") => true,
                ("charUnboundMgr", "GetCharacterLockType") => null,
                ("populationCap", "MachoResolveObject") => PilotLogin.NodeId,
                ("populationCap", "MachoBindObject") when IsPilotLoadSlot(call[2]) => new object?[] { new BlueObjectReference($"N={PilotLogin.NodeId}:1", 1), new Dictionary<string, object?>() },
                _ => s_unsupported,
            };
            if (ReferenceEquals(result, s_unsupported))
            {
                await write(Response(state, "ErrorResponse", 15, new object?[] { 6, 0, "Unsupported local service call" }));
                continue;
            }

            await write(Response(state, "CallRsp", 7, new object?[] { BlueMarshal.Encode(result) }));
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

    private static readonly object s_unsupported = new();

    private static bool IsPilotLoadSlot(object? value) =>
        value is object?[] { Length: 2 } args && args[0] is object?[] { Length: 2 } bind && bind[0] is (long)PilotLogin.PilotId &&
        args[1] is object?[] { Length: > 1 } call && BlueMarshal.Text(call[0]) == "GetCharacterLoadSlot" &&
        call[1] is object?[] { Length: 1 } ids && ids[0] is (long)PilotLogin.PilotId;

    private static BlueInstance Response(object?[] state, string name, int command, object? body) => new(PacketNamespace + name,
        new object?[] { command, state[2], state[1], state[3], body, new Dictionary<string, object?>(), null, null, state[8], null, state[10], state[11], state[12], null });

    private static object?[] OwnerRows(object? arguments)
    {
        var ids = PilotLogin.Tuple(arguments, 1)[0] as List<object?> ?? throw new InvalidDataException("Expected owner IDs.");
        List<object?> rows = [];
        foreach (object? id in ids)
        {
            if (id is (long)PilotLogin.PilotId)
            {
                rows.Add(new object?[] { PilotLogin.PilotId, "Eden Pilot", 1373, 1, null });
            }
        }

        return [new object?[] { "ownerID", "ownerName", "typeID", "gender", "ownerNameID" }, rows];
    }

    private static object?[] CharacterSelection()
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
            ["characterID"] = PilotLogin.PilotId,
            ["characterName"] = "Eden Pilot",
            ["name"] = "Eden Pilot",
            ["stationID"] = 60003760,
            ["solarSystemID"] = 30000142,
            ["shipTypeID"] = 601,
            ["corporationID"] = 1000044,
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
