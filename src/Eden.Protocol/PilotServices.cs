namespace Eden.Protocol;

internal static class PilotServices
{
    private const string PacketNamespace = "carbon.common.script.net.machoNetPacket.";
    private static BlueInstance Address(params object?[] state) => new("carbon.common.script.net.machoNetAddress.MachoAddress", state);

    public static async Task RunAsync(Func<Task<object?>> read, Func<object?, Task> write, Action<string> report)
    {
        while (true)
        {
            if (await read() is not BlueInstance packet) { throw new InvalidDataException("Expected a MachoNet packet."); }
            object?[] state = PilotLogin.Tuple(packet.State, 14);
            if (packet.Name == PacketNamespace + "PingReq")
            {
                await write(Response(state, "PingRsp", 21, state[4]));
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

            string service = BlueMarshal.Text(route[0] is 1L ? route[2] : route[1]);
            string method = BlueMarshal.Text(call[1]);
            report($"Call {service}.{method}.");
            object? result = (service, method) switch
            {
                ("machoNet", "GetServiceInfo") => new Dictionary<string, object?>(),
                ("machoNet", "GetTime") => DateTime.UtcNow.ToFileTimeUtc(),
                ("charUnboundMgr", "GetCharacterSelectionData") => CharacterSelection(),
                ("config", "GetMultiOwnersEx") => OwnerRows(call[2]),
                ("config", "GetMultiCorpTickerNamesEx") => new object?[] { new object?[] { "corporationID", "tickerName" }, new List<object?> { new object?[] { 1000044, "SWA" } } },
                ("config", "GetBlackListedPlanets" or "GetOldStationData" or "GetAverageMarketPrices") => new List<object?>(),
                ("invbroker", "GetItemDescriptor") => new List<object?> { new object?[] { "itemID", 20, 8 }, new object?[] { "typeID", 3, 4 } },
                ("air_npe", "is_air_npe_enabled") => false,
                ("subscriptionMgr", "GetCloneGrade") => 0,
                _ => null,
            };
            await write(Response(state, "CallRsp", 7, new object?[] { BlueMarshal.Encode(result) }));
            if (method == "GetServiceInfo")
            {
                Dictionary<string, object?> initial = new() { ["userid"] = PilotLogin.UserId, ["role"] = 1, ["languageID"] = "EN", ["userType"] = 1 };
                await write(new BlueInstance(PacketNamespace + "SessionInitialStateNotification", new object?[]
                {
                    18, Address(1, PilotLogin.NodeId, null, null), Address(2, PilotLogin.ClientId, null, null), PilotLogin.UserId,
                    new object?[] { PilotLogin.ClientId, 5, initial }, new Dictionary<string, object?>(), null, null, null, null, null, null, null, null,
                }));
            }
        }
    }

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
            ["paperdollState"] = 1,
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
