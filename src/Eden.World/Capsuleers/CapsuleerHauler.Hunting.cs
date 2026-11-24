using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private const double HuntGridTolerance = PatrolRules.PursuitMeters;

    private IEnumerable<StaticSpaceObject> HuntLocations() =>
        Objects.Where(site => site.DestinationGateId is not null || site.GroupId == AsteroidDefinitions.BeltGroup);

    private long[] HuntGrids(long sequence)
    {
        long lead = HuntReport()?.GridId ?? 0;
        return HuntLocations()
            .Where(site => site.Id == lead || !Work.VisitedBelts.Any(visit => visit.BeltId == site.Id && visit.Until > Work.ActiveTicks))
            .OrderByDescending(site => site.Id == lead)
            .ThenBy(site => TravelerRoutineRules.Mix((ulong)site.Id ^ (ulong)pilotId ^ (ulong)sequence))
            .Take(PatrolRules.MaximumGrids)
            .Select(site => site.Id)
            .ToArray();
    }

    private RegionalConflictRegion? LocalReports(out int group)
    {
        group = 0;
        if (
            !database.Settings.Capsuleers.RegionalConflicts
            || Live is not { } live
            || RegionalConflictStore.HomeFor(live, database.Catalog, pilotId) is not { } home
        )
        {
            return null;
        }
        group = home.GroupId;
        return live.Economy.Peek<SavedRegionalConflict>(database.Catalog.Systems[home.SystemId].RegionId)?.State;
    }

    private RegionalReport? HuntReport() =>
        LocalReports(out var group)
            ?.Reports.Where(report =>
                report.ObserverGroupId == group
                && report.SystemId == Pilot.SystemId
                && report.ExpiresAt > Work.ActiveTicks
                && report.Kind is RegionalReportKind.Freight or RegionalReportKind.Mining or RegionalReportKind.CombatShip
            )
            .MaxBy(report => report.SeenAt);

    private bool HeardFreightWarning(StaticSpaceObject gate)
    {
        if (
            !database.Settings.Capsuleers.SituationalAwareness
            || Work.Retreating
            || Work.Freight?.Stacks.Length is not > 0
            || gate.DestinationSystemId is not { } destination
            || !CapsuleerTrafficRoutes.IsNullsec(database.Catalog, destination)
            || LocalReports(out var group) is not { } region
        )
        {
            return false;
        }

        return region.Reports.Any(report =>
            report.ObserverGroupId == group
            && report.SystemId == destination
            && report.GridId == gate.DestinationGateId
            && report.ExpiresAt > Work.ActiveTicks
            && report.Kind is RegionalReportKind.Attack or RegionalReportKind.Loss
        );
    }

    private long[] SearchNextGrid(RattingProgress activity)
    {
        if (_operation.World is not EconomyTransaction world || Pilot.Position is not { } origin)
        {
            return activity.Belts;
        }

        int next = activity.BeltIndex + 1;
        var checkedGrids = activity.Belts.Take(next).ToHashSet();
        var contacts = information
            .DirectionalShips(world, _operation.Catalog, Pilot, Now)
            .Where(contact => PatrolRules.ContactInterest((HullClass)contact.GroupId) > 0)
            .ToArray();
        if (contacts.Length == 0)
        {
            return activity.Belts;
        }
        var lead = HuntLocations()
            .Where(grid => !checkedGrids.Contains(grid.Id) && origin.DistanceTo(grid.Position) > HuntGridTolerance)
            .Select(grid =>
                (
                    grid.Id,
                    Score: contacts
                        .Where(contact =>
                            grid.Position.DistanceTo(contact.Position) <= HuntGridTolerance
                            && InformationRules.InCone(
                                origin,
                                contact.Position,
                                grid.Position - origin,
                                PatrolRules.SearchConeRadians,
                                InformationRules.MaximumDirectionalRange
                            )
                        )
                        .Select(contact => PatrolRules.ContactInterest((HullClass)contact.GroupId))
                        .DefaultIfEmpty()
                        .Max()
                )
            )
            .Where(grid => grid.Score > 0)
            .OrderByDescending(grid => grid.Score)
            .ThenBy(grid => grid.Id)
            .FirstOrDefault();
        if (lead.Id == 0 || lead.Id == activity.Belts[next])
        {
            return activity.Belts;
        }
        var route = (long[])activity.Belts.Clone();
        int old = Array.IndexOf(route, lead.Id, next);
        if (old >= 0)
        {
            route[old] = route[next];
        }
        route[next] = lead.Id;
        return route;
    }
}
