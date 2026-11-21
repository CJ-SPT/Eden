using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Information;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Trading;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class DockedCompatibilityHandler(
    IPilotFleet fleet,
    IPilotAssets assets,
    DirectionalScanHandler scanner,
    Eden.Gameplay.Structures.IPilotStructures structures,
    PlayerTradeHandler trades,
    ICargoContainers containers,
    Eden.Gameplay.Information.IPublicLocations publicLocations
) : ISelectedPilotCallHandler
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
            ("ProjectDiscovery", "get_player_analysis_kredits") when args.Length == 0 => 0,
            // Build 3662168 OpenLpstore probes event presence before creating the store tabs.
            ("RWManager", "solarsystem_contains_rw_instances") when args.Length == 0 && keywords is null or { Count: 0 } => false,
            ("RWManager", "solarsystem_contains_rw_instances") => new PilotCallResult.Failure(
                "Read Resource Wars presence for your current system without additional arguments."
            ),
            ("clientStatLogger", "LogString") => PilotCallResult.None,
            ("shipKillCounter", "GetItemKillCountPlayer") => 0,
            ("userSvc", "GetMultiCharactersTrainingSlots") => BlueEmpty.Dictionary,
            ("config", "GetMultiLocationsEx") when args is [List<object?> ids] => LocationsWithAssetNames(
                pilot.PilotId,
                ids,
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
                                    new(pilot.PilotId, pilot.ShipId, pilot.DockedLocationId, pilot.Position is null),
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
                    .Concat(scanner.Names(context, ids.OfType<long>().ToHashSet()))
                    .Concat(trades.Locations(ids.OfType<long>().ToHashSet()))
                    .Concat(
                        containers
                            .Names(pilot.PilotId, ids.OfType<long>().ToHashSet())
                            .Select(item => new LocationRow(item.ItemId, item.Name, null, 0, 0, 0))
                    )
                    .Concat(
                        structures
                            .Names(ids.OfType<long>().Where(id => id >= Eden.Gameplay.Structures.StructureRules.FirstStructureId).ToArray())
                            .Select(s => new LocationRow(s.Id, s.Name, s.SystemId, s.Position.X, s.Position.Y, s.Position.Z))
                    )
            ),
            ("shipCosmeticsMgr", "GetEnabledCosmetics") => BlueEmpty.Dictionary,
            ("@ship", "GetAvailableTurretSlots") => pilot.TurretSlots,
            ("stationSvc", "GetStationItemBits") => new StationItemBits(pilot),
            ("warRegistry", "GetWars") => BlueEmpty.Dictionary,
            ("air_npe", "get_air_npe_state") => 0,
            ("fighterMgr", "GetFightersForShip") => new FighterState(),
            ("map", "GetTriglavianMinorVictorySystems" or "GetEdencomFortressSystems" or "GetEdencomMinorVictorySystems") => BlueEmpty.List,
            ("operationsManager", "can_character_play_the_tutorial") => false,
            ("standingMgr", "GetNPCNPCStandings") => Rows(["fromID", "toID", "standing"]),


            ("seasonManager", "get_season_data_for_character")
            or
            ("eventLog", "LogClientStats")
            or
            ("ProjectDiscovery", "initialize_tutorial_status")
            or ("milestoneMgr", "ProcessCharacterLogon") => PilotCallResult.None,

            ("securityMgr", "get_modified_systems") => BlueEmpty.Dictionary,

            ("insurgencySolarsystem", "GetAllVisibleCampaigns") => BlueEmpty.List,
            _ => PilotCallResult.Unsupported,
        };
    }

    private LocationTable LocationsWithAssetNames(long pilotId, List<object?> ids, IEnumerable<LocationRow> known)
    {
        var locations = known.DistinctBy(location => location.Id).ToList();
        var missing = ids.OfType<long>().Except(locations.Select(location => location.Id)).ToHashSet();
        locations.AddRange(
            publicLocations
                .Read(missing.ToArray())
                .Select(location => new LocationRow(
                    location.Id,
                    location.Name,
                    location.SystemId,
                    location.Position.X,
                    location.Position.Y,
                    location.Position.Z
                ))
        );
        missing.ExceptWith(locations.Select(location => location.Id));
        // Personal Assets primes names before rendering. The cached row only needs the item's name.
        locations.AddRange(
            assets.StationItemNames(pilotId, missing).Select(asset => new LocationRow(asset.ItemId, asset.Name, null, 0, 0, 0))
        );
        return new(locations);
    }
}
