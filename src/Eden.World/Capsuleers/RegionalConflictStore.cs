using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class RegionalConflictDiagnostics(WorldDatabase database) : IRegionalConflictDiagnostics
{
    public IReadOnlyList<RegionalConflictRegion> ReadConflicts() =>
        LiveWorld.Find(database.Path) is { } live
            ? live.Economy.Read(world => world.Set<SavedRegionalConflict>().OrderBy(row => row.RegionId).Select(row => row.State).ToArray())
            : [];
}

internal static class RegionalConflictStore
{
    private const int RivalsPerGroup = 2;

    internal sealed record Home(int GroupId, int SystemId, int StationId);

    internal static Home? HomeFor(LiveWorld live, CatalogProvider catalog, long pilot)
    {
        var agent = live.Economy.Peek<SavedCapsuleerAgent>(pilot);
        if (
            agent is null
            || catalog.SystemSecurity.GetValueOrDefault(agent.CommunitySystemId, 1) > 0
            || !catalog.Systems.TryGetValue(agent.CommunitySystemId, out var system)
            || live.Economy.Peek<SavedCapsuleerPopulation>(system.Id) is not { } community
        )
        {
            return null;
        }
        return new(system.ConstellationId, system.Id, community.HomeStationId);
    }

    internal static void Attack(
        CapsuleerOperation action,
        WorldDatabase database,
        DockedPilot pilot,
        CapsuleerDefenseState? state,
        long grid,
        long elapsed
    )
    {
        if (
            !database.Settings.Capsuleers.RegionalConflicts
            || LiveWorld.Find(database.Path) is not { } live
            || state is null
            || live.Economy.ActivePilot(state.TargetId) is not { } attacker
            || HomeFor(live, database.Catalog, pilot.PilotId) is not { } home
        )
        {
            return;
        }
        var report = new RegionalReport(
            home.GroupId,
            GroupForPilot(live, database.Catalog, attacker),
            attacker,
            state.TargetId,
            pilot.SystemId,
            grid,
            RegionalReportKind.Attack,
            elapsed,
            checked(elapsed + RegionalConflictRules.ReportTicks)
        );
        if (NeedsReport(database, home.SystemId, report))
        {
            Report(action.World, action.Catalog, home.StationId, report);
        }
    }

    internal static int? GroupForPilot(LiveWorld live, CatalogProvider catalog, long pilot)
    {
        var agent = live.Economy.Peek<SavedCapsuleerAgent>(pilot);
        if (
            agent is null
            || catalog.SystemSecurity.GetValueOrDefault(agent.CommunitySystemId, 1) > 0
            || !catalog.Systems.TryGetValue(agent.CommunitySystemId, out var system)
        )
        {
            return null;
        }
        return system.ConstellationId;
    }

    internal static RegionalOperation? Operation(WorldDatabase database, RegionalOperationKey key) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Peek<SavedRegionalConflict>(key.RegionId)
            ?.State.Operations.FirstOrDefault(operation => operation.Key == key);

    internal static RegionalConflictRegion Ensure(WorldAction world, CatalogDbContext catalog, int station, long elapsed)
    {
        var system = catalog.RequireSystem(catalog.RequireStation(station).SolarSystemId);
        return Ensure(world, catalog, new Home(system.ConstellationId, system.Id, station), elapsed);
    }

    private static RegionalConflictRegion Ensure(WorldAction world, CatalogDbContext catalog, Home home, long elapsed)
    {
        // Stationless communities can use a refuge in another region. Their affiliation still belongs to their home system.
        var system = catalog.RequireSystem(home.SystemId);
        var row =
            world.Set<SavedRegionalConflict>().Find(system.RegionId)
            ?? world.Add(new SavedRegionalConflict { RegionId = system.RegionId, State = new(system.RegionId, 0) }).Entity;
        var region = RegionalConflictRules.Advance(row.State, elapsed);
        string name = catalog.Definitions.Constellations.GetValueOrDefault(system.ConstellationId)?.Name ?? system.Name;
        region = RegionalConflictRules.Discover(region, new(home.GroupId, $"{name} patrol", system.Id, home.StationId));
        row.State = region;
        return region;
    }

    internal static RegionalOperation? Plan(WorldAction world, CatalogDbContext catalog, LiveWorld live, int station, long elapsed)
    {
        var source = catalog.RequireSystem(catalog.RequireStation(station).SolarSystemId);
        var region = Ensure(world, catalog, station, elapsed);
        var journeys = CapsuleerTrafficRoutes.RegionalRoutes(catalog, station);
        List<RegionalRoute> routes = [];
        foreach (var journey in journeys.Prepend(new(station, station, [source.Id])))
        {
            int target = journey.Systems[^1];
            var targetSystem = catalog.RequireSystem(target);
            if (targetSystem.RegionId != source.RegionId || world.Set<SavedCapsuleerPopulation>().Find(target) is null)
            {
                continue;
            }
            string name = catalog.Definitions.Constellations.GetValueOrDefault(targetSystem.ConstellationId)?.Name ?? targetSystem.Name;
            region = RegionalConflictRules.Discover(
                region,
                new(targetSystem.ConstellationId, $"{name} patrol", target, journey.DestinationStationId)
            );
            var objects = SystemObjects.Geometry(catalog, target);
            long gate = objects
                .Where(body => body.DestinationGateId is not null)
                .OrderBy(body => body.Id)
                .Select(body => body.Id)
                .FirstOrDefault();
            long belt = objects
                .Where(body => body.GroupId == AsteroidDefinitions.BeltGroup)
                .OrderBy(body => body.Id)
                .Select(body => body.Id)
                .FirstOrDefault();
            if (gate != 0 || belt != 0)
            {
                routes.Add(new(targetSystem.ConstellationId, journey.DestinationStationId, target, journey.Systems, gate, belt));
            }
        }
        foreach (
            int rival in routes
                .Where(route => route.GroupId != source.ConstellationId)
                .Select(route => route.GroupId)
                .Distinct()
                .Take(RivalsPerGroup)
        )
        {
            region = RegionalConflictRules.Compete(region, source.ConstellationId, rival);
        }
        var escorts = world
            .Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == source.Id && agent.Career == CapsuleerCareer.Hauling)
            .Select(agent => agent.PilotId)
            .Take(RegionalConflictRules.MaximumReports)
            .AsEnumerable()
            .Select(pilot => live.HaulerCheckpoints.GetValueOrDefault(pilot))
            .OfType<HaulerCheckpoint>()
            .Where(checkpoint =>
                checkpoint.Work.RegionalEscort is null
                && world.Set<SavedCapsuleerSuspension>().Find(checkpoint.PilotId) is null
                && world.Pilots.Find(checkpoint.PilotId)?.ActiveShipId == checkpoint.Work.ShipId
                && !checkpoint.Work.Retreating
                && checkpoint.Work.RouteIndex == 0
                && world.Positions.Find(checkpoint.Work.ShipId)?.SystemId == source.Id
                && checkpoint.Work.Freight is { Stacks.Length: > 0 }
                && checkpoint.Work.Journey is { } trip
                && trip.SourceStationId == station
                && trip.Systems.Length is > 1 and <= RoamingGangRules.MaximumJumps + 1
                && trip.Systems.All(system => catalog.Definitions.SystemSecurity.GetValueOrDefault(system, 1) <= 0)
            )
            .Where(checkpoint => GroupForPilot(live, catalog.Definitions, checkpoint.PilotId) == source.ConstellationId)
            .OrderBy(checkpoint => checkpoint.PilotId)
            .Take(RegionalConflictRules.MaximumReports)
            .Select(checkpoint => new RegionalEscortCandidate(
                checkpoint.PilotId,
                checkpoint.Work.ShipId,
                checkpoint.Work.Journey!.DestinationStationId,
                checkpoint.Work.Journey.Systems,
                catalog
                    .Definitions.GatesInSystem(checkpoint.Work.Journey.Systems[^1])
                    .OrderBy(gate => gate.Id)
                    .Select(gate => (long)gate.Id)
                    .FirstOrDefault()
            ))
            .ToArray();
        world.Set<SavedRegionalConflict>().Find(source.RegionId)!.State = region;
        return RegionalConflictRules.Plan(
            region,
            source.ConstellationId,
            routes,
            escorts,
            elapsed,
            world.Settings.Capsuleers.RegionalOperationsPerGroup
        );
    }

    internal static void Launch(WorldAction world, RegionalOperation operation, RoamingGang gang)
    {
        var row = world.Set<SavedRegionalConflict>().Find(operation.Key.RegionId)!;
        row.State = RegionalConflictRules.Launch(row.State, operation, gang.Id, gang.Sequence);
    }

    internal static void Finish(WorldAction world, RegionalOperationKey key, bool success, string? reason = null)
    {
        if (
            world.Set<SavedRegionalConflict>().Find(key.RegionId) is { } row
            && row.State.Operations.Any(operation => operation.Key == key && operation.Status == RegionalOperationStatus.Active)
        )
        {
            row.State = RegionalConflictRules.Finish(row.State, key, success, reason);
        }
    }

    internal static void Reached(WorldAction world, RegionalOperationKey key)
    {
        var row = world.Set<SavedRegionalConflict>().Find(key.RegionId)!;
        if (row.State.Operations.Any(operation => operation.Key == key && !operation.ReachedObjective))
        {
            row.State = row.State with
            {
                Revision = checked(row.State.Revision + 1),
                Operations = row
                    .State.Operations.Select(operation => operation.Key == key ? operation with { ReachedObjective = true } : operation)
                    .ToArray(),
            };
        }
    }

    internal static bool NeedsReport(WorldDatabase database, int homeSystem, RegionalReport report) =>
        LiveWorld.Find(database.Path)?.Economy.Peek<SavedRegionalConflict>(database.Catalog.Systems[homeSystem].RegionId)?.State
            is not { } region
        || RegionalConflictRules.NeedsReport(region, report);

    internal static void Report(WorldAction world, CatalogDbContext catalog, int homeStation, RegionalReport report)
    {
        var region = Ensure(world, catalog, homeStation, report.SeenAt);
        if (
            report.OtherGroupId is { } other
            && report.Kind is RegionalReportKind.Attack or RegionalReportKind.Loss
            && region.Groups.Any(group => group.Id == other)
        )
        {
            region = RegionalConflictRules.Compete(region, report.ObserverGroupId, other);
        }
        world.Set<SavedRegionalConflict>().Find(region.RegionId)!.State = RegionalConflictRules.Report(region, report);
    }

    internal static void Loss(
        WorldAction world,
        CatalogDbContext catalog,
        long pilot,
        long ship,
        int system,
        Eden.Gameplay.Flight.SpacePosition position,
        long? attacker
    )
    {
        if (
            world is not EconomyTransaction
            || !world.Settings.Capsuleers.Enabled
            || !world.Settings.Capsuleers.RegionalConflicts
            || LiveWorld.Find(world.WorldPath) is not { } live
        )
        {
            return;
        }
        var victimHome = HomeFor(live, catalog.Definitions, pilot);
        var attackerHome = attacker is { } killer ? HomeFor(live, catalog.Definitions, killer) : null;
        var homes = new[] { victimHome, attackerHome }.OfType<Home>().ToArray();
        if (homes.Length == 0)
        {
            return;
        }
        long elapsed = world.ActiveTicks;
        foreach (var home in homes)
        {
            Ensure(world, catalog, home, elapsed);
        }
        long grid = SystemObjects
            .Geometry(catalog, system)
            .Where(body => body.GroupId == AsteroidDefinitions.BeltGroup || body.DestinationGateId is not null)
            .Where(body => body.Position.DistanceTo(position) <= PatrolRules.PursuitMeters)
            .OrderBy(body => body.Position.DistanceTo(position))
            .Select(body => body.Id)
            .FirstOrDefault();
        foreach (int regionId in homes.Select(home => catalog.RequireSystem(home.SystemId).RegionId).Distinct())
        {
            var row = world.Set<SavedRegionalConflict>().Find(regionId)!;
            if (row.State.RecentLosses.Contains(ship))
            {
                continue;
            }
            int? victimGroup =
                victimHome is not null && catalog.RequireSystem(victimHome.SystemId).RegionId == regionId ? victimHome.GroupId : null;
            int? attackerGroup =
                attackerHome is not null && catalog.RequireSystem(attackerHome.SystemId).RegionId == regionId ? attackerHome.GroupId : null;
            var region = RegionalConflictRules.Loss(row.State, victimGroup, attackerGroup, ship, elapsed);
            foreach (
                var escort in region.Operations.Where(operation =>
                    operation.Status == RegionalOperationStatus.Active
                    && operation.Objective == RegionalObjective.Escort
                    && operation.TargetPilotId == pilot
                )
            )
            {
                region = RegionalConflictRules.Finish(region, escort.Key, false, "The escorted ship was destroyed.");
            }
            if (victimGroup is { } group && attacker is { } aggressor)
            {
                if (attackerGroup is { } rival)
                {
                    region = RegionalConflictRules.Compete(region, group, rival);
                }
                region = RegionalConflictRules.Report(
                    region,
                    new(
                        group,
                        attackerGroup,
                        aggressor,
                        ship,
                        system,
                        grid,
                        RegionalReportKind.Loss,
                        elapsed,
                        checked(elapsed + RegionalConflictRules.ReportTicks)
                    )
                );
            }
            row.State = region;
        }
    }

    internal static bool CanTarget(WorldDatabase database, RegionalOperation operation, PilotSighting contact, long elapsed)
    {
        if (contact.RegionalGroupId == operation.Key.GroupId)
        {
            return false;
        }
        var region = LiveWorld.Find(database.Path)?.Economy.Peek<SavedRegionalConflict>(operation.Key.RegionId)?.State;
        bool reportedAttacker =
            region?.Reports.Any(report =>
                report.ObserverGroupId == operation.Key.GroupId
                && report.PilotId == contact.PilotId
                && report.ExpiresAt > elapsed
                && report.Kind is RegionalReportKind.Attack or RegionalReportKind.Loss
            ) == true;
        return reportedAttacker
            || contact.RegionalGroupId is { } other
                && region is not null
                && RegionalConflictRules.Rivals(region, operation.Key.GroupId, other)
                && operation.Objective is RegionalObjective.Raid or RegionalObjective.Intercept
            || operation.Objective == RegionalObjective.Defend && operation.TargetPilotId == contact.PilotId;
    }
}
