using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

// Bindings live only for this authenticated connection. A nested MachoBindObject call
// returns both the broker reference and the result of its first method invocation.
internal sealed class DockedServices(DockedPilot initialPilot, long sessionId, FlightSession? flight = null)
{
    private DockedPilot Pilot => flight?.Pilot ?? initialPilot;
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
            "ship" => [Pilot.ShipId, Pilot.ShipTypeId, Pilot.PilotId, Pilot.LocationId, Pilot.Position is null ? 4 : 0, -1, Pilot.ShipGroupId, Pilot.ShipCategoryId, "", 1, 1],
            "pilot" => [Pilot.PilotId, 1373, Pilot.PilotId, Pilot.ShipId, 57, -1, 1, 1, "", 1, 1],
            _ => [Pilot.StationId, Pilot.StationTypeId, Pilot.StationOwnerId, Pilot.SystemId, 0, -1, 15, 3, "", 1, 1],
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
            // Undamaged, fully charged Ibis. The HUD reads these current values from godma.
            attributes[9] = 175;
            attributes[18] = 130;
            attributes[37] = (double)Ballpark.MaxSpeed;
            attributes[38] = Pilot.CargoCapacity;
            attributes[55] = 65000;
            attributes[263] = 200;
            attributes[264] = 200;
            attributes[265] = 125;
            attributes[479] = 860000;
            attributes[482] = 130;
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
        if (service is "invbroker" or "dogmaIM" or "corpRegistry" or "crimewatch" or "warRegistry" or "ship" or "insuranceSvc" or "beyonce")
        {
            if (method == "MachoResolveObject") { return PilotLogin.NodeId; }
            if (method == "MachoBindObject" && args.Length == 2)
            {
                long locationGroup = Pilot.Position is null ? 15 : 5;
                if (service is "invbroker" or "ship" && args[0] is not null &&
                    (args[0] is not object?[] bind || bind is not [long location, long group] || location != Pilot.LocationId || group != locationGroup))
                {
                    return Unsupported;
                }
                if (service == "beyonce" && (Pilot.Position is null || !Equals(args[0], (long)Pilot.SystemId)))
                {
                    return Unsupported;
                }
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
            ("account", "GetCashBalance") => 0d,
            ("keeper", "GetCurrentDungeonForCharacter") => null,
            ("dogmaIM", "GetTargets" or "GetTargeters") => new List<object?>(),
            ("home_station", "get_home_station") => KeyVal(new()
            {
                ["id"] = Pilot.StationId,
                ["type_id"] = Pilot.StationTypeId,
                ["solar_system_id"] = Pilot.SystemId,
                ["is_fallback"] = false,
            }),
            ("scanMgr", "SignalTrackerRegister") => null,
            ("contractProxy", "GetLoginInfo") => KeyVal(new()
            {
                ["assignedToMe"] = new List<object?>(),
                ["needsAttention"] = new List<object?>(),
                ["inProgress"] = new List<object?>(),
            }),
            ("@skills", "GetSkillHistory") => new List<object?>(),
            ("bountyProxy", "GetBountiesAndKillRights") => new object?[] { new List<object?>(), new List<object?>() },
            ("bountyProxy", "GetMyKillRights") => new List<object?>(),
            ("monolithTaskTrackerMgr", "HasEverWarped") => false,
            ("essMgr", "GetDataForClientSolarSystem") => null,
            ("insurgencySolarsystem", "GetLocalCampaignClientSnapshot") => new object?[] { Pilot.SystemId, null },
            ("monolithTaskTrackerMgr", "UpdateClientAchievmentsAndCounters") => null,
            ("crimewatch", "GetMySecurityStatus") => 0d,
            ("notificationMgr", "GetAllNotifications") => new List<object?>(),
            ("beyonce", "GetFormations") => Array.Empty<object?>(),
            ("charMgr", "SetActivityStatus") => null,
            ("@skills", "GetSkillQueueAndFreePoints") => new object?[] { new List<object?>(), 0 },
            ("corpRegistry", "GetAggressionSettings") => null,
            ("corpRegistry", "GetEveOwners") => Rows(["ownerID", "ownerName", "typeID"], [Pilot.PilotId, Pilot.Name, 1373]),
            ("dynamicBountyMgr", "GetOutputForClientSolarSystem") => new object?[] { 1d, false },
            ("ProjectDiscovery", "is_enabled") => false,
            ("clientStatLogger", "LogString") => null,
            ("onlineStatus", "GetInitialState") => Rows(["contactID", "online"]),
            ("ship", "GetDirtTimestamp") when args.Length == 1 && Equals(args[0], Pilot.ShipId) => DateTime.UtcNow.ToFileTimeUtc(),
            ("shipKillCounter", "GetItemKillCountPlayer") => 0,
            ("userSvc", "GetMultiCharactersTrainingSlots") => new Dictionary<string, object?>(),
            ("charMgr", "GetContactList") => KeyVal(new() { ["addresses"] = new List<object?>(), ["blocked"] = new List<object?>() }),
            ("corpRegistry", "GetCorporateContacts") => new List<object?>(),
            ("config", "GetMultiLocationsEx") when args is [List<object?> ids] => new object?[]
            {
                new object?[] { "locationID", "locationName", "solarSystemID", "x", "y", "z", "locationNameID" },
                ids.Contains(Pilot.ShipId) ? new List<object?> { new object?[] { Pilot.ShipId, Pilot.ShipName, Pilot.SystemId,
                    Pilot.Position?.X ?? Pilot.X, Pilot.Position?.Y ?? Pilot.Y, Pilot.Position?.Z ?? Pilot.Z, null } } : new List<object?>(),
            },
            ("XmppChatMgr", "Hostname") => "127.0.0.1",
            ("shipCosmeticsMgr", "GetEnabledCosmetics") or ("@skills", "GetSkills") => new Dictionary<string, object?>(),
            ("@ship", "GetAvailableTurretSlots") => Pilot.TurretSlots,
            ("@skills", "GetBoosters" or "GetImplants") or ("nonDiminishingInjectionMgr", "GetAvailableNonDiminishingInjections") => new Dictionary<long, object?>(),
            ("@skills", "CheckAndSendNotifications") or ("XmppChatMgr", "GetDeprecatedPrefsFallback") => null,
            ("corpRegistry", "GetMember") when args is [long id] && id == Pilot.PilotId =>
                KeyVal(new() { ["characterID"] = id, ["corporationID"] = Pilot.CorpId, ["title"] = "", ["roles"] = 0, ["titleMask"] = 0 }),
            ("insuranceSvc", "GetContracts") => new List<object?>(),
            ("stationSvc", "GetStationItemBits") => new object?[] { Pilot.StationOwnerId, Pilot.StationId, Pilot.OperationId, Pilot.StationTypeId },
            ("station", "GetGuests") => new List<object?> { new object?[] { Pilot.PilotId, Pilot.CorpId, null, null } },
            ("warRegistry", "GetWars") or ("corpRegistry", "GetMyApplications") => new Dictionary<string, object?>(),
            ("crimewatch", "GetClientStates") => new object?[] {
                Enumerable.Range(0, 5).Select(_ => (object?)new object?[] { null, null }).ToArray(),
                new Dictionary<string, object?>(), new object?[] { new List<object?>(), new List<object?>() }, 2 }, // Full ship safety is required by the flight HUD.
            ("jumpTimers", "GetTimers") => new object?[] { null, null, null },
            ("air_npe", "get_air_npe_state") => 0,
            ("agentMgr", "GetAgents") => Rows(["agentID", "stationID", "corporationID", "solarsystemID", "agentTypeID", "factionID", "divisionID", "level", "quality", "isLocator"]),
            ("fighterMgr", "GetFightersForShip") => new object?[] { new List<object?>(), new List<object?>(), new Dictionary<string, object?>() },
            ("map", "GetStationInfo") => Rows(["stationID", "stationTypeID", "stationName", "solarSystemID", "constellationID",
                "regionID", "corporationID", "ownerID", "operationID", "orbitID", "x", "y", "z", "serviceMask"],
                [Pilot.StationId, Pilot.StationTypeId, "Jita IV - Moon 4 - Caldari Navy Assembly Plant", Pilot.SystemId,
                 Pilot.ConstellationId, Pilot.RegionId, Pilot.StationOwnerId, Pilot.StationOwnerId, Pilot.OperationId,
                 Pilot.OrbitId, Pilot.X, Pilot.Y, Pilot.Z, 0]),
            ("invbroker", "GetInventory") when Pilot.Position is null && args is [10004L, ..] => Reference("@hangar"),
            ("invbroker", "GetInventoryFromId") when args.Length > 0 && Equals(args[0], Pilot.ShipId) => Reference("@ship"),
            ("invbroker", "GetInventoryFromId") when args.Length > 0 && Equals(args[0], Pilot.PilotId) => Reference("@pilot"),
            ("@hangar", "List") => Pilot.Position is null && (args.Length == 0 || args[0] is null or 4L) ? new List<object?> { Item("ship") } : new List<object?>(),
            ("@ship" or "@pilot", "List") => new List<object?>(),
            ("@hangar" or "@ship" or "@pilot", "GetSelfInvItem") => Item(service[1..]),
            ("skillMgr2", "GetMySkillHandler") => Reference("@skills"),
            ("dogmaIM", "GetAllInfo") => KeyVal(new()
            {
                // Instance rows follow dgmAttributesByIdx: online, damage, charge, SP, armor damage, shield, incapacitated.
                ["shipState"] = new object?[] { new Dictionary<long, object?>
                {
                    [Pilot.PilotId] = new object?[] { Pilot.PilotId, 0, 0, 0, 0, 0, 0, 0 },
                    [Pilot.ShipId] = new object?[] { Pilot.ShipId, 0, 0, 130, 0, 0, 200, 0 },
                }, new Dictionary<long, object?>(), new Dictionary<long, object?>(), new List<object?>() },
                ["structureInfo"] = null,
                ["shipInfo"] = PrimeItem(Pilot.ShipId, "ship"),
                ["charInfo"] = new object?[] { PrimeItem(Pilot.PilotId, "pilot"), new object?[] { 0, Array.Empty<object?>(), Array.Empty<object?>(), Array.Empty<object?>() } },
                ["shipModifiedCharAttribs"] = null,
                ["systemWideEffectsOnShip"] = null,
            }),
            ("operationsManager", "can_character_play_the_tutorial") => false,
            ("standingMgr", "GetNPCNPCStandings") => Rows(["fromID", "toID", "standing"]),
            ("agentMgr", "GetMyJournalDetails") => new object?[] { new List<object?>(), new List<object?>() },
            ("accessGroupBookmarkMgr", "GetMyActiveBookmarks") => new object?[] { new List<object?> { KeyVal(new()
            {
                ["folderID"] = 1, ["folderName"] = "Personal Locations", ["isPersonal"] = true, ["creatorID"] = Pilot.PilotId,
                ["ownerID"] = Pilot.PilotId, ["isActive"] = true, ["description"] = "",
            }) }, new List<object?>(), new List<object?>() },
            ("monolithTaskTrackerMgr", "GetCompletedAchievementsAndClientEventCount") => new Dictionary<string, object?>
            { ["completedDict"] = new Dictionary<string, object?>(), ["eventDict"] = new Dictionary<string, object?>() },
            ("fwWarzoneSolarsystem", "GetLocalOccupationState") => new object?[] { Pilot.SystemId, null },
            ("seasonManager", "get_season_data_for_character") or ("eventLog", "LogClientStats") or
            ("ProjectDiscovery", "initialize_tutorial_status") or ("milestoneMgr", "ProcessCharacterLogon") => null,
            ("standingMgr", "GetCharStandings" or "GetCorpStandings") or ("charMgr", "GetCharacterSettings") or
            ("securityMgr", "get_modified_systems") => new Dictionary<string, object?>(),
            ("structureDirectory", "GetMyDockableStructures") or
            ("officeManager", "GetMyCorporationsOffices") or ("insurgencySolarsystem", "GetAllVisibleCampaigns") or
            ("calendarProxy", "GetEventList") => new List<object?>(),
            _ => flight is null ? Unsupported : flight.Call(service, method, args),
        };
    }
}
