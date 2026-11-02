using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class DockedCompatibilityHandler(TimeProvider clock) : ISelectedPilotCallHandler
{
    private static BlueInstance KeyVal(Dictionary<string, object?> values) => new("utillib.KeyVal", values);
    private static BlueInstance Rows(string[] header, params object?[][] lines) => new("eve.common.script.sys.rowset.Rowset",
        new Dictionary<string, object?> { ["header"] = header.Cast<object?>().ToList(), ["lines"] = lines.Cast<object?>().ToList() });

    public object? Call(PilotCallContext context, string service, string method, object?[] args)
    {
        var pilot = context.Pilot;
        return (service, method) switch
        {
            ("charMgr", "GetSettingsInfo") => context.SignedSettings,
            ("account", "GetCashBalance") => 0d,
            ("keeper", "GetCurrentDungeonForCharacter") => null,
            ("dogmaIM", "GetTargets" or "GetTargeters") => new List<object?>(),
            ("home_station", "get_home_station") => KeyVal(new()
            {
                ["id"] = pilot.StationId,
                ["type_id"] = pilot.StationTypeId,
                ["solar_system_id"] = pilot.SystemId,
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
            ("insurgencySolarsystem", "GetLocalCampaignClientSnapshot") => new object?[] { pilot.SystemId, null },
            ("monolithTaskTrackerMgr", "UpdateClientAchievmentsAndCounters") => null,
            ("crimewatch", "GetMySecurityStatus") => 0d,
            ("notificationMgr", "GetAllNotifications") => new List<object?>(),
            ("beyonce", "GetFormations") => Array.Empty<object?>(),
            ("charMgr", "SetActivityStatus") => null,
            ("@skills", "GetSkillQueueAndFreePoints") => new object?[] { new List<object?>(), 0 },
            ("corpRegistry", "GetAggressionSettings") => null,
            ("corpRegistry", "GetEveOwners") => Rows(["ownerID", "ownerName", "typeID"], [pilot.PilotId, pilot.Name, 1373]),
            ("dynamicBountyMgr", "GetOutputForClientSolarSystem") => new object?[] { 1d, false },
            ("ProjectDiscovery", "is_enabled") => false,
            ("clientStatLogger", "LogString") => null,
            ("onlineStatus", "GetInitialState") => Rows(["contactID", "online"]),
            ("ship", "GetDirtTimestamp") when args.Length == 1 && Equals(args[0], pilot.ShipId) => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            ("shipKillCounter", "GetItemKillCountPlayer") => 0,
            ("userSvc", "GetMultiCharactersTrainingSlots") => new Dictionary<string, object?>(),
            ("charMgr", "GetContactList") => KeyVal(new() { ["addresses"] = new List<object?>(), ["blocked"] = new List<object?>() }),
            ("corpRegistry", "GetCorporateContacts") => new List<object?>(),
            ("config", "GetMultiLocationsEx") when args is [List<object?> ids] => new object?[]
            {
                new object?[] { "locationID", "locationName", "solarSystemID", "x", "y", "z", "locationNameID" },
                context.Flight.VisiblePilots().Where(owner => ids.Contains(owner.ShipId)).Select(owner => (object?)new object?[]
                {
                    owner.ShipId, owner.ShipName, owner.SystemId, owner.Position?.X ?? owner.X,
                    owner.Position?.Y ?? owner.Y, owner.Position?.Z ?? owner.Z, null,
                }).ToList(),
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
                new Dictionary<string, object?>(), new object?[] { new List<object?>(), new List<object?>() }, 2 }, // Full ship safety is required by the flight HUD.
            ("jumpTimers", "GetTimers") => new object?[] { null, null, null },
            ("air_npe", "get_air_npe_state") => 0,
            ("agentMgr", "GetAgents") => Rows(["agentID", "stationID", "corporationID", "solarsystemID", "agentTypeID", "factionID", "divisionID", "level", "quality", "isLocator"]),
            ("fighterMgr", "GetFightersForShip") => new object?[] { new List<object?>(), new List<object?>(), new Dictionary<string, object?>() },
            ("map", "GetStationInfo") => Rows(["stationID", "stationTypeID", "stationName", "solarSystemID", "constellationID",
                "regionID", "corporationID", "ownerID", "operationID", "orbitID", "x", "y", "z", "serviceMask"],
                [pilot.StationId, pilot.StationTypeId, pilot.Station.Name, pilot.SystemId,
                 pilot.ConstellationId, pilot.RegionId, pilot.StationOwnerId, pilot.StationOwnerId, pilot.OperationId,
                 pilot.OrbitId, pilot.X, pilot.Y, pilot.Z, 0]),
            ("skillMgr2", "GetMySkillHandler") => context.Reference("@skills"),
            ("operationsManager", "can_character_play_the_tutorial") => false,
            ("standingMgr", "GetNPCNPCStandings") => Rows(["fromID", "toID", "standing"]),
            ("agentMgr", "GetMyJournalDetails") => new object?[] { new List<object?>(), new List<object?>() },
            ("accessGroupBookmarkMgr", "GetMyActiveBookmarks") => new object?[] { new List<object?> { KeyVal(new()
            {
                ["folderID"] = 1, ["folderName"] = "Personal Locations", ["isPersonal"] = true, ["creatorID"] = pilot.PilotId,
                ["ownerID"] = pilot.PilotId, ["isActive"] = true, ["description"] = "",
            }) }, new List<object?>(), new List<object?>() },
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
            _ => PilotCallRouter.Unsupported,
        };
    }
}
