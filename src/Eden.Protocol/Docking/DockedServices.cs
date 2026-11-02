using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

// Bindings live only for this authenticated connection. A nested MachoBindObject call
// returns both the broker reference and the result of its first method invocation.
internal sealed class DockedServices(DockedPilot pilot, long sessionId)
{
    internal static readonly object Unsupported = new();
    private readonly Dictionary<string, string> _bindings = [];
    private readonly Dictionary<string, BlueObjectReference> _references = [];

    public string? ResolveReference(object? objectId) =>
        objectId is byte[] or string ? _bindings.GetValueOrDefault(BlueMarshal.Text(objectId)) : null;

    private BlueObjectReference Reference(string service)
    {
        if (!_references.TryGetValue(service, out var reference))
        {
            int slot = _references.Count + 2;
            reference = new($"N={PilotLogin.NodeId}:{sessionId}-{slot}", slot);
            _references.Add(service, reference);
            _bindings.Add(reference.Id, service);
        }
        return reference;
    }

    private static BlueInstance KeyVal(Dictionary<string, object?> values) => new("utillib.KeyVal", values);
    private static BlueInstance Rows(string[] header, params object?[][] lines) => new("eve.common.script.sys.rowset.Rowset",
        new Dictionary<string, object?> { ["header"] = header.Cast<object?>().ToList(), ["lines"] = lines.Cast<object?>().ToList() });

    private static readonly (string Name, int Type)[] s_columns =
    [
        ("itemID", 20), ("typeID", 3), ("ownerID", 3), ("locationID", 20),
        ("flagID", 2), ("quantity", 3), ("groupID", 3), ("categoryID", 3),
        ("customInfo", 129), ("singleton", 2), ("stacksize", 3),
    ];

    internal static BlueConstructor ItemDescriptor() => new("blue.DBRowDescriptor",
        [s_columns.Select(column => (object?)new object?[] { column.Name, column.Type }).ToArray()]);

    private BlueConstructor Item(string kind)
    {
        object?[] line = kind switch
        {
            "ship" => [pilot.ShipId, pilot.ShipTypeId, pilot.PilotId, pilot.StationId, 4, -1, pilot.ShipGroupId, pilot.ShipCategoryId, "", 1, 1],
            "pilot" => [pilot.PilotId, 1373, pilot.PilotId, pilot.ShipId, 57, -1, 1, 1, "", 1, 1],
            _ => [pilot.StationId, pilot.StationTypeId, pilot.StationOwnerId, pilot.SystemId, 0, -1, 15, 3, "", 1, 1],
        };
        return new("blue.DBRow", [ItemDescriptor(), line]);
    }

    private Dictionary<long, object?> PrimeItem(long id, string kind)
    {
        long now = DateTime.UtcNow.ToFileTimeUtc();
        var attributes = new Dictionary<long, object?>();
        if (kind == "pilot")
        {
            foreach (long attribute in new long[] { 164, 165, 166, 167, 168 })
            {
                attributes.Add(attribute, 20);
            }
        }

        else
        {
            attributes[38] = pilot.CargoCapacity;
        }
        return new()
        {
            [id] = KeyVal(new()
            {
                ["itemID"] = id,
                ["invItem"] = Item(kind),
                ["attributes"] = attributes,
                ["activeEffects"] = new Dictionary<long, object?>(),
                ["time"] = now,
                ["wallclockTime"] = now
            })
        };
    }

    public object? Call(string service, string method, object?[] args, Action<string> report)
    {
        if (service is "invbroker" or "dogmaIM" or "corpRegistry" or "crimewatch" or "warRegistry" or "ship" or "insuranceSvc")
        {
            if (method == "MachoResolveObject") { return PilotLogin.NodeId; }
            if (method == "MachoBindObject" && args.Length == 2)
            {
                if (service == "invbroker" && (args[0] is not object?[] bind || bind is not [long station, 15L] || station != pilot.StationId)) { return Unsupported; }
                object? result = null;
                if (args[1] is object?[] { Length: >= 2 } first && first[1] is object?[] innerArgs)
                {
                    string innerMethod = BlueMarshal.Text(first[0]);
                    report($"Bound call {service}.{innerMethod}.");
                    result = Call(service, innerMethod, innerArgs, report);
                }
                else if (args[1] is not null) { return Unsupported; }
                return ReferenceEquals(result, Unsupported) ? Unsupported : new object?[] { Reference(service), result };
            }
        }
        return (service, method) switch
        {
            ("@skills", "GetSkillQueueAndFreePoints") => new object?[] { new List<object?>(), 0 },
            ("corpRegistry", "GetAggressionSettings") => null,
            ("corpRegistry", "GetEveOwners") => Rows(["ownerID", "ownerName", "typeID"], [pilot.PilotId, pilot.Name, 1373]),
            ("dynamicBountyMgr", "GetOutputForClientSolarSystem") => new object?[] { 1d, false },
            ("ProjectDiscovery", "is_enabled") => false,
            ("clientStatLogger", "LogString") => null,
            ("onlineStatus", "GetInitialState") => Rows(["contactID", "online"]),
            ("ship", "GetDirtTimestamp") when args.Length == 1 && Equals(args[0], pilot.ShipId) => DateTime.UtcNow.ToFileTimeUtc(),
            ("shipKillCounter", "GetItemKillCountPlayer") => 0,
            ("userSvc", "GetMultiCharactersTrainingSlots") => new Dictionary<string, object?>(),
            ("charMgr", "GetContactList") => KeyVal(new() { ["addresses"] = new List<object?>(), ["blocked"] = new List<object?>() }),
            ("corpRegistry", "GetCorporateContacts") => new List<object?>(),
            ("config", "GetMultiLocationsEx") when args is [List<object?> ids] => new object?[]
            {
                new object?[] { "locationID", "locationName", "solarSystemID", "x", "y", "z", "locationNameID" },
                ids.Contains(pilot.ShipId) ? new List<object?> { new object?[] { pilot.ShipId, pilot.ShipName, pilot.SystemId,
                    pilot.X, pilot.Y, pilot.Z, null } } : new List<object?>(),
            },
            ("XmppChatMgr", "Hostname") => "127.0.0.1",
            ("shipCosmeticsMgr", "GetEnabledCosmetics") or ("@skills", "GetSkills") => new Dictionary<string, object?>(),
            ("@ship", "GetAvailableTurretSlots") => pilot.TurretSlots,
            ("@skills", "GetBoosters" or "GetImplants") or ("nonDiminishingInjectionMgr", "GetAvailableNonDiminishingInjections") => new Dictionary<long, object?>(),
            ("@skills", "CheckAndSendNotifications") or ("XmppChatMgr", "GetDeprecatedPrefsFallback") => null,
            ("corpRegistry", "GetMember") when args is [long id] && id == pilot.PilotId =>
                KeyVal(new() { ["characterID"] = id, ["corporationID"] = pilot.CorpId, ["title"] = "", ["roles"] = 0, ["titleMask"] = 0 }),
            ("insuranceSvc", "GetContracts") => new List<object?>(),
            ("stationSvc", "GetStationItemBits") => new object?[] { pilot.StationOwnerId, pilot.StationId, pilot.OperationId, pilot.StationTypeId },
            ("station", "GetGuests") => new List<object?> { new object?[] { pilot.PilotId, pilot.CorpId, null, null } },
            ("warRegistry", "GetWars") or ("corpRegistry", "GetMyApplications") => new Dictionary<string, object?>(),
            ("crimewatch", "GetClientStates") => new object?[] {
                Enumerable.Range(0, 5).Select(_ => (object?)new object?[] { null, null }).ToArray(),
                new Dictionary<string, object?>(), new object?[] { new List<object?>(), new List<object?>() }, null },
            ("jumpTimers", "GetTimers") => new object?[] { null, null, null },
            ("air_npe", "get_air_npe_state") => 0,
            ("agentMgr", "GetAgents") => Rows(["agentID", "stationID", "corporationID", "solarsystemID", "agentTypeID", "factionID", "divisionID", "level", "quality", "isLocator"]),
            ("fighterMgr", "GetFightersForShip") => new object?[] { new List<object?>(), new List<object?>(), new Dictionary<string, object?>() },
            ("map", "GetStationInfo") => Rows(["stationID", "stationTypeID", "stationName", "solarSystemID", "constellationID",
                "regionID", "corporationID", "ownerID", "operationID", "orbitID", "x", "y", "z", "serviceMask"],
                [pilot.StationId, pilot.StationTypeId, "Jita IV - Moon 4 - Caldari Navy Assembly Plant", pilot.SystemId,
                 pilot.ConstellationId, pilot.RegionId, pilot.StationOwnerId, pilot.StationOwnerId, pilot.OperationId,
                 pilot.OrbitId, pilot.X, pilot.Y, pilot.Z, 0]),
            ("invbroker", "GetInventory") when args is [10004L, ..] => Reference("@hangar"),
            ("invbroker", "GetInventoryFromId") when args.Length > 0 && Equals(args[0], pilot.ShipId) => Reference("@ship"),
            ("invbroker", "GetInventoryFromId") when args.Length > 0 && Equals(args[0], pilot.PilotId) => Reference("@pilot"),
            ("@hangar", "List") => args.Length == 0 || args[0] is null or 4L ? new List<object?> { Item("ship") } : new List<object?>(),
            ("@ship" or "@pilot", "List") => new List<object?>(),
            ("@hangar" or "@ship" or "@pilot", "GetSelfInvItem") => Item(service[1..]),
            ("skillMgr2", "GetMySkillHandler") => Reference("@skills"),
            ("dogmaIM", "GetAllInfo") => KeyVal(new()
            {
                ["shipState"] = new object?[] { new Dictionary<long, object?>(), new Dictionary<long, object?>(), new Dictionary<long, object?>(), new List<object?>() },
                ["structureInfo"] = null,
                ["shipInfo"] = PrimeItem(pilot.ShipId, "ship"),
                ["charInfo"] = new object?[] { PrimeItem(pilot.PilotId, "pilot"), null },
                ["shipModifiedCharAttribs"] = null,
                ["systemWideEffectsOnShip"] = null,
            }),
            ("operationsManager", "can_character_play_the_tutorial") => false,
            ("standingMgr", "GetNPCNPCStandings") => Rows(["fromID", "toID", "standing"]),
            ("agentMgr", "GetMyJournalDetails") => new object?[] { new List<object?>(), new List<object?>() },
            ("accessGroupBookmarkMgr", "GetMyActiveBookmarks") => new object?[] { new List<object?>(), new List<object?>(), new List<object?>() },
            ("monolithTaskTrackerMgr", "GetCompletedAchievementsAndClientEventCount") => new Dictionary<string, object?>
            { ["completedDict"] = new Dictionary<string, object?>(), ["eventDict"] = new Dictionary<string, object?>() },
            ("fwWarzoneSolarsystem", "GetLocalOccupationState") => new object?[] { pilot.SystemId, null },
            ("seasonManager", "get_season_data_for_character") or ("eventLog", "LogClientStats") or
            ("ProjectDiscovery", "initialize_tutorial_status") or ("milestoneMgr", "ProcessCharacterLogon") => null,
            ("standingMgr", "GetCharStandings" or "GetCorpStandings") or ("charMgr", "GetCharacterSettings") or
            ("securityMgr", "get_modified_systems") => new Dictionary<string, object?>(),
            ("structureDirectory", "GetMyDockableStructures") or
            ("officeManager", "GetMyCorporationsOffices") or ("insurgencySolarsystem", "GetAllVisibleCampaigns") or
            ("calendarProxy", "GetEventList") => new List<object?>(),
            _ => Unsupported,
        };
    }
}
