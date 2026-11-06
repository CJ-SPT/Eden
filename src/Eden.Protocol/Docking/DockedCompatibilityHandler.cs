using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class DockedCompatibilityHandler(IPilotFleet fleet, IPilotSafety safety) : ISelectedPilotCallHandler
{
    private static CompatibilityRowset Rows(string[] header, params IBlueWritable[] lines) => new(header, lines);

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        var pilot = context.Pilot;
        return (service, method) switch
        {
            ("charMgr", "GetSettingsInfo") => context.SignedSettings,
            ("keeper", "GetCurrentDungeonForCharacter") => PilotCallResult.None,
            ("scanMgr", "SignalTrackerRegister") => PilotCallResult.None,
            ("bountyProxy", "GetBountiesAndKillRights") => new BountiesAndKillRightsState(),
            ("bountyProxy", "GetBounties") when args is [HashSet<long> owners] => new BlueList<BountyReply>(
                owners.Order().Select(owner => new BountyReply(owner)).ToArray()
            ),
            ("bountyProxy", "GetMyKillRights") when args.Length == 0 => BlueEmpty.List,
            ("bountyProxy", "GetMyKillRights") => BlueEmpty.List,
            ("essMgr", "GetDataForClientSolarSystem") => PilotCallResult.None,
            ("insurgencySolarsystem", "GetLocalCampaignClientSnapshot") => new LocalSystemState(pilot.SystemId),
            ("beyonce", "GetFormations") => BlueEmpty.Tuple,
            ("charMgr", "SetActivityStatus") => PilotCallResult.None,
            ("dynamicBountyMgr", "GetOutputForClientSolarSystem") => new DynamicBountyState(),
            ("ProjectDiscovery", "is_enabled") => false,
            ("clientStatLogger", "LogString") => PilotCallResult.None,
            ("shipKillCounter", "GetItemKillCountPlayer") => 0,
            ("userSvc", "GetMultiCharactersTrainingSlots") => BlueEmpty.Dictionary,
            ("corpRegistry", "GetCorporateContacts") => BlueEmpty.List,
            ("config", "GetMultiLocationsEx") when args is [List<object?> ids] => new LocationTable(
                context
                    .Flight.VisiblePilots()
                    .Where(owner => ids.Contains(owner.ShipId))
                    .Select(owner => new LocationRow(
                        owner.ShipId,
                        owner.ShipName,
                        owner.SystemId,
                        owner.Position?.X ?? owner.X,
                        owner.Position?.Y ?? owner.Y,
                        owner.Position?.Z ?? owner.Z
                    ))
                    .Concat(
                        ids.OfType<long>()
                            .Except(context.Flight.VisiblePilots().Select(p => p.ShipId))
                            .Select(id => fleet.Find(pilot.PilotId, id))
                            .OfType<InventoryItem>()
                            .Where(ship =>
                                ShipStorageRules.CanRead(
                                    new(pilot.PilotId, pilot.ShipId, pilot.StationId, pilot.Position is null),
                                    ship,
                                    fleet.Find(pilot.PilotId, ship.LocationId)
                                )
                                || context.Flight.VisibleWrecks.Any(wreck => wreck.OwnerId == pilot.PilotId && wreck.Id == ship.LocationId)
                            )
                            .Select(ship => new LocationRow(
                                ship.Id,
                                fleet.Name(pilot.PilotId, ship.Id),
                                pilot.SystemId,
                                pilot.X,
                                pilot.Y,
                                pilot.Z
                            ))
                    )
                    .Concat(
                        context
                            .Flight.VisibleNpcs.Where(n => ids.Contains(n.Id))
                            .Select(n => new LocationRow(n.Id, n.Name, n.SystemId, n.Position.X, n.Position.Y, n.Position.Z))
                    )
                    .Concat(
                        context
                            .Flight.VisibleWrecks.Where(w => ids.Contains(w.Id))
                            .Select(w => new LocationRow(w.Id, w.Name, w.SystemId, w.Position.X, w.Position.Y, w.Position.Z))
                    )
                    .ToArray()
            ),
            ("shipCosmeticsMgr", "GetEnabledCosmetics") => BlueEmpty.Dictionary,
            ("@ship", "GetAvailableTurretSlots") => pilot.TurretSlots,
            ("stationSvc", "GetStationItemBits") => new StationItemBits(pilot),
            ("warRegistry", "GetWars") => BlueEmpty.Dictionary,
            ("crimewatch", "GetClientStates") when args.Length == 0 => new CrimewatchClientState(safety.Read(pilot.PilotId)),
            ("crimewatch", "SetSafetyLevel") => SetSafety(pilot.PilotId, args),
            ("jumpTimers", "GetTimers") => new JumpTimerState(),
            ("air_npe", "get_air_npe_state") => 0,
            ("fighterMgr", "GetFightersForShip") => new FighterState(),
            ("map", "GetTriglavianMinorVictorySystems" or "GetEdencomFortressSystems" or "GetEdencomMinorVictorySystems") => BlueEmpty.List,
            ("operationsManager", "can_character_play_the_tutorial") => false,
            ("standingMgr", "GetNPCNPCStandings") => Rows(["fromID", "toID", "standing"]),
            ("agentMgr", "GetMyJournalDetails") => new AgentJournalState(),
            ("fwWarzoneSolarsystem", "GetLocalOccupationState") => new LocalSystemState(pilot.SystemId),


            ("seasonManager", "get_season_data_for_character")
            or
            ("eventLog", "LogClientStats")
            or
            ("ProjectDiscovery", "initialize_tutorial_status")
            or ("milestoneMgr", "ProcessCharacterLogon") => PilotCallResult.None,

            ("securityMgr", "get_modified_systems") => BlueEmpty.Dictionary,


            ("structureDirectory", "GetMyDockableStructures" or "GetStructureMapData")
            or
            ("officeManager", "GetMyCorporationsOffices")
            or ("insurgencySolarsystem", "GetAllVisibleCampaigns") => BlueEmpty.List,
            _ => PilotCallResult.Unsupported,
        };
    }

    private PilotCallResult SetSafety(long pilotId, object?[] args)
    {
        if (args is not [long level] || level is < (long)ShipSafetyLevel.Disabled or > (long)ShipSafetyLevel.Full)
        {
            return new PilotCallResult.Failure("Choose full, partial or disabled ship safety.");
        }
        safety.Set(pilotId, (ShipSafetyLevel)level);
        // The pinned client applies its selection and emits OnSafetyLevelChanged
        // after this RPC succeeds. A separate server event would duplicate it.
        return PilotCallResult.None;
    }
}
