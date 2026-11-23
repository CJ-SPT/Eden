using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.FactionWarfare;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private const int MilitiaRecruitmentJumps = 6;
    private const int MilitiaSortieJumps = 4;
    private const int MilitiaStageLimit = 8;
    private const int MilitiaCandidateLimit = 6;
    private const int MaximumMilitiaPatrolsPerFaction = 2;
    private static readonly long s_militiaReviewInterval = TimeSpan.FromMinutes(3).Ticks;

    private sealed record MilitiaDispatch(
        MilitiaFaction Faction,
        int StagingStationId,
        int[] StagingPath,
        int[] SortiePath,
        ulong Draw,
        bool Contested
    );

    private bool TryAssignMilitiaPatrol(LiveWorld live, EconomyTransaction world)
    {
        var catalog = _operation.Catalog;
        var factions = MilitiaEnlistmentStore.BotMilitiaFactions(world, catalog, pilotId, Now);
        if (factions.Length == 0)
        {
            return MilitiaBlocked("No allowed militia enlistment.");
        }
        var pilot = world.Pilots.Find(pilotId)!;
        var enlisted = MilitiaEnlistmentStore.Effective(world, pilot);
        var hull = (HullClass)Pilot.ShipGroupId;
        var avoided = MilitiaAvoidedSystems();
        var policy = world.Settings.Capsuleers;
        var origin = CapsuleerTrafficRoutes.Nearby(catalog, Pilot.SystemId, hull, MilitiaRecruitmentJumps, avoided);
        // Complex availability and occupation are read from the resident warzone at dispatch time.
        var fronts = origin
            .Systems.Select(entry => FactionWarzoneStore.System(world, entry.SystemId))
            .OfType<WarzoneSystem>()
            .Where(front => front.Adjacency == WarzoneAdjacency.Frontline && policy.CanWork(catalog.Definitions, front.SystemId))
            .ToArray();
        if (fronts.Length == 0)
        {
            return MilitiaBlocked("No reachable frontline within patrol range.");
        }

        var active = live
            .MilitiaPatrolPilots.Where(other => other != pilotId && world.Set<SavedCapsuleerSuspension>().Find(other) is null)
            .Select(other => live.HaulerCheckpoints.GetValueOrDefault(other)?.Work.MilitiaPatrol)
            .OfType<MilitiaPatrolProgress>()
            .Where(patrol => patrol.Phase != MilitiaPatrolPhase.Returning)
            .ToArray();
        factions = factions.Where(faction => active.Count(patrol => patrol.Faction == faction) < MaximumMilitiaPatrolsPerFaction).ToArray();
        if (factions.Length == 0)
        {
            return MilitiaBlocked("The eligible militia patrols are fully staffed.");
        }

        long seed = world.Set<SavedCapsuleerProvisioning>().Find(1)?.WorldSeed ?? 0;
        ulong draw = TravelerRoutineRules.Mix((ulong)seed ^ (ulong)pilotId ^ TravelerRoutineRules.Mix((ulong)Work.DwellSequence));
        var stages = origin
            .Systems.Where(entry => enlisted is null || entry.SystemId == Pilot.SystemId)
            .Where(entry => policy.CanWork(catalog.Definitions, entry.SystemId))
            .SelectMany(entry => catalog.Definitions.StationsInSystem(entry.SystemId).Select(station => (entry.Jumps, Station: station)))
            .Select(entry =>
                (
                    entry.Jumps,
                    entry.Station,
                    Faction: enlisted ?? MilitiaEnlistmentStore.EnlistmentStationFaction(catalog, entry.Station.OwnerId)
                )
            )
            .Where(entry =>
                entry.Faction is { } faction
                && factions.Contains(faction)
                && MilitiaRules.CanUseDocking(FactionWarzoneStore.System(world, entry.Station.SolarSystemId), faction, capsule: false)
            )
            .OrderBy(entry => entry.Station.Id != Pilot.StationId)
            .ThenBy(entry => entry.Jumps)
            .ThenBy(entry => TravelerRoutineRules.Mix(draw ^ (uint)entry.Station.Id))
            .DistinctBy(entry => (entry.Station.SolarSystemId, entry.Faction, entry.Station.Id == Pilot.StationId))
            .Take(MilitiaStageLimit)
            .ToArray();

        if (stages.Length == 0)
        {
            return MilitiaBlocked("No reachable NPC station permits militia staging.");
        }

        List<MilitiaDispatch> dispatches = [];
        foreach (var stage in stages)
        {
            var faction = stage.Faction!.Value;
            var stagingPath = origin.PathTo(stage.Station.SolarSystemId, draw)!;
            var staging = CapsuleerTrafficRoutes.Nearby(catalog, stage.Station.SolarSystemId, hull, MilitiaSortieJumps, avoided);
            foreach (var front in fronts.Where(front => front.Warzone.FirstFaction == faction || front.Warzone.SecondFaction == faction))
            {
                if (staging.PathTo(front.SystemId, draw) is { } sortie)
                {
                    dispatches.Add(
                        new(
                            faction,
                            stage.Station.Id,
                            stagingPath,
                            sortie,
                            TravelerRoutineRules.Mix(draw ^ (uint)front.SystemId),
                            active.Any(patrol => patrol.SystemId == front.SystemId && MilitiaRules.AreEnemies(faction, patrol.Faction))
                        )
                    );
                }
            }
        }

        bool reviewedLocalFit = false;
        foreach (
            var dispatch in dispatches
                .OrderByDescending(dispatch => dispatch.Contested)
                .ThenBy(dispatch => dispatch.StagingStationId != Pilot.StationId)
                .ThenBy(dispatch => dispatch.StagingPath.Length + dispatch.SortiePath.Length)
                .ThenBy(dispatch => dispatch.Draw)
                .Take(MilitiaCandidateLimit)
        )
        {
            int target = dispatch.SortiePath[^1];
            if (militia.MilitiaPatrolFitPolicy(world, catalog, target, Now) is not { } allowedFit)
            {
                continue;
            }
            bool local = dispatch.StagingStationId == Pilot.StationId;
            bool ready = TryPrepareMilitiaFit(world, target, allowedFit, dispatch.SortiePath, buyFit: local && !reviewedLocalFit);
            reviewedLocalFit |= local;
            CapsuleerPvpFitGoal? stagingFit = null;
            if (
                !ready
                && !local
                && !dispatch.StagingPath.Any(system => catalog.Definitions.SystemSecurity.GetValueOrDefault(system, 1) <= 0)
            )
            {
                stagingFit = ammunitionSupply.MilitiaFitAtStation(
                    world,
                    catalog,
                    database.Traffic,
                    Pilot,
                    dispatch.StagingStationId,
                    target,
                    allowedFit
                );
            }
            if (!ready && stagingFit is null)
            {
                continue;
            }
            var sites = stagingFit is null
                ? militia.MilitiaPatrolSites(world, catalog, pilotId, target, Now)
                : militia.MilitiaPatrolSites(
                    world,
                    catalog,
                    target,
                    Now,
                    stagingFit.HullTypeId,
                    stagingFit.Slots.Select(slot => slot.TypeId).ToArray()
                );
            var site = sites
                .OrderByDescending(site =>
                    active.Any(patrol => patrol.SiteId == site.Id && MilitiaRules.AreEnemies(dispatch.Faction, patrol.Faction))
                )
                .ThenBy(site => TravelerRoutineRules.Mix(dispatch.Draw ^ (ulong)site.Id))
                .FirstOrDefault();
            if (site is null)
            {
                continue;
            }
            var patrol = new MilitiaPatrolProgress(
                Pilot.ShipId,
                dispatch.Faction,
                Pilot.StationId,
                site,
                local ? MilitiaPatrolPhase.Travelling : MilitiaPatrolPhase.Staging,
                checked(Work.ActiveTicks + s_militiaPatrolDuration)
            )
            {
                StagingStationId = dispatch.StagingStationId,
                RouteDraw = dispatch.Draw,
                StagingFit = stagingFit,
            };
            if (local)
            {
                return LaunchMilitiaSortie(world, patrol, dispatch.SortiePath);
            }
            StartMilitiaFlight(patrol, new(Pilot.StationId, dispatch.StagingStationId, dispatch.StagingPath));
            return true;
        }
        return MilitiaBlocked("No reachable Scout or Small complex has a ready fit within the patrol's budget.");
    }

    private bool ContinueMilitiaStaging(EconomyTransaction world, MilitiaPatrolProgress patrol)
    {
        var catalog = _operation.Catalog;
        if (
            Pilot.StationId != patrol.StagingStationId
            || !MilitiaPatrolActive(patrol)
            || !MilitiaEnlistmentStore.BotMilitiaFactions(world, catalog, pilotId, Now).Contains(patrol.Faction)
            || !MilitiaRules.CanUseDocking(FactionWarzoneStore.System(world, Pilot.SystemId), patrol.Faction, capsule: false)
            || CapsuleerTrafficRoutes
                .Nearby(catalog, Pilot.SystemId, (HullClass)Pilot.ShipGroupId, MilitiaSortieJumps, MilitiaAvoidedSystems())
                .PathTo(patrol.SystemId, patrol.RouteDraw)
                is not { } path
            || militia.MilitiaPatrolFitPolicy(world, catalog, patrol.SystemId, Now) is not { } allowedFit
        )
        {
            return MilitiaBlocked("The assigned frontline, route, or enlistment is no longer available.");
        }
        if (!TryPrepareMilitiaFit(world, patrol.SystemId, allowedFit, path, buyFit: true, patrol.StagingFit))
        {
            return MilitiaBlocked("The staging market or owned fit could not supply the patrol.");
        }

        var site = militia
            .MilitiaPatrolSites(world, catalog, pilotId, patrol.SystemId, Now)
            .FirstOrDefault(site => site.Id == patrol.SiteId);
        return site is not null
            && LaunchMilitiaSortie(world, patrol with { Site = site, Phase = MilitiaPatrolPhase.Travelling, StagingFit = null }, path);
    }

    private bool TryPrepareMilitiaFit(
        EconomyTransaction world,
        int targetSystem,
        Func<int, IEnumerable<int>, bool> allowedFit,
        int[] path,
        bool buyFit,
        CapsuleerPvpFitGoal? stagedFit = null
    )
    {
        var catalog = _operation.Catalog;
        var profile = CapsuleerDefenseProfile.Read(world, catalog, pilotId);
        if (
            !allowedFit(Pilot.ShipTypeId, world.FittedItems(pilotId, Pilot.ShipId).Select(item => item.TypeId))
            || !profile.Combat
            || profile.RattingWeapons.Length == 0
        )
        {
            if (!buyFit)
            {
                return false;
            }
            var acquisition = ammunitionSupply.TryBuyEarnedPvpShip(
                world,
                catalog,
                database.Traffic,
                Pilot,
                Now,
                stagingSystem: targetSystem,
                visitsHighSecurity: path.Any(system =>
                    TrafficRules.SecurityBand(catalog.Definition(CapsuleerProvisioningCatalog.Security, system)) == TrafficSecurityBand.High
                ),
                allowedMilitiaFit: allowedFit,
                stagedMilitiaFit: stagedFit
            );
            _operation.MarketChanged |= acquisition.MarketChanged;
            if (!acquisition.Boarded)
            {
                return false;
            }
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
            ResetTradeHull();
        }

        var supplies = resupply.Dock(database, world, catalog, pilotId, Work.Resupply, Now, out var fit);
        _operation.MarketChanged |= supplies.SpentCents != (Work.Resupply?.SpentCents ?? 0);
        _work = Work with { Resupply = supplies };
        profile = CapsuleerDefenseProfile.Read(world, catalog, pilotId, fit);
        return profile.Combat
            && profile.RattingWeapons.Any(weapon => weapon.AvailableShots > 0)
            && !CapsuleerDefenseRules.Retreat(
                PilotHealthStore.Read(world, Pilot.ShipId, ShipHealth.DefenseFrom(profile.ShipAttributes), Now),
                true
            );
    }

    private bool LaunchMilitiaSortie(EconomyTransaction world, MilitiaPatrolProgress patrol, int[] path)
    {
        var pilot = world.Pilots.Find(pilotId)!;
        bool alreadyEnlisted = MilitiaEnlistmentStore.Effective(world, pilot) is not null;
        if (!MilitiaEnlistmentStore.TryJoinBotMilitia(world, _operation.Catalog, pilotId, patrol.Faction, Now))
        {
            return MilitiaBlocked("The assigned militia no longer permits enlistment.");
        }
        if (!alreadyEnlisted)
        {
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
            world.AfterCommit(database.NotifyAccessGroupsChanged);
        }
        // The acceleration gate is the endpoint. Use the staging station as the return anchor even for stationless fronts.
        StartMilitiaFlight(
            patrol with
            {
                ShipId = Pilot.ShipId,
                HomeStationId = Pilot.StationId,
            },
            new(Pilot.StationId, Pilot.StationId, path)
        );
        return true;
    }

    private HashSet<int> MilitiaAvoidedSystems() =>
        (Work.Routine?.Threats ?? []).Where(threat => threat.ExpiresAt > Work.ActiveTicks).Select(threat => threat.SystemId).ToHashSet();

    private bool MilitiaBlocked(string reason)
    {
        _work = Work with { MilitiaWaitingReason = reason };
        return false;
    }
}
