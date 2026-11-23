using Eden.Gameplay.Agents;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.FactionWarfare;

internal sealed partial class MilitiaComplexStore
{
    internal IReadOnlyList<MilitiaPatrolSite> MilitiaPatrolSites(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int systemId,
        long now
    )
    {
        if (world.Pilots.Find(pilotId) is not { } pilot || world.Ships.Find(pilot.ActiveShipId) is not { } ship)
        {
            return [];
        }
        int[] fitted = world.FittedItems(pilotId, ship.Id).Select(item => item.TypeId).ToArray();
        return MilitiaPatrolSites(world, catalog, systemId, now, ship.TypeId, fitted);
    }

    internal IReadOnlyList<MilitiaPatrolSite> MilitiaPatrolSites(
        WorldAction world,
        CatalogDbContext catalog,
        int systemId,
        long now,
        int hull,
        IReadOnlyList<int> fitted
    )
    {
        return PatrolSites(world, systemId, now)
            .Where(site => AdmitsPatrolFit(catalog, site.Profile.Access!, hull, fitted))
            .Select(site => new MilitiaPatrolSite(
                site.Id,
                site.System.SystemId,
                site.System.Occupier,
                site.Profile.Variant.Family,
                site.Profile.Gate! with
                {
                    Id = MilitiaComplexRules.GateId(site.Id),
                    Position = site.Gate,
                },
                site.Profile.Beacon with
                {
                    Id = MilitiaComplexRules.BeaconId(site.Id),
                    Position = site.Beacon,
                },
                site.Profile.CapturePoint with
                {
                    Id = MilitiaComplexRules.PointId(site.Id),
                    Position = site.Capture,
                }
            ))
            .ToArray();
    }

    internal Func<int, IEnumerable<int>, bool>? MilitiaPatrolFitPolicy(WorldAction world, CatalogDbContext catalog, int system, long now)
    {
        var access = PatrolSites(world, system, now).Select(site => site.Profile.Access!).ToArray();
        return access.Length == 0 ? null : (hull, fitted) => access.Any(gate => AdmitsPatrolFit(catalog, gate, hull, fitted));
    }

    internal bool MilitiaPatrolSiteCurrent(WorldAction world, MilitiaPatrolSite patrol, long now) =>
        PatrolSites(world, patrol.SystemId, now).Any(site => site.Id == patrol.Id && site.System.Occupier == patrol.Occupier);

    internal SpacePosition ActivateMilitiaPatrolGate(WorldAction world, CatalogDbContext catalog, long pilotId, long gateId, long now)
    {
        var gate = ValidateGate(world, catalog, pilotId, gateId, now);
        if (gate.Neutral || !PatrolSite(gate.Site))
        {
            throw new InvalidDataException("This gate is unavailable to the militia patrol.");
        }
        return gate.Site.Beacon;
    }

    private IEnumerable<Site> PatrolSites(WorldAction world, int system, long now) => Read(world, system, now).Where(PatrolSite);

    private static bool PatrolSite(Site site) =>
        site.System.Adjacency == WarzoneAdjacency.Frontline
        && site.Profile.Gate is not null
        && site.Profile.Variant.Family is MilitiaComplexFamily.Scout or MilitiaComplexFamily.Small;

    private static bool AdmitsPatrolFit(CatalogDbContext catalog, MissionGateAccess access, int hull, IEnumerable<int> fitted) =>
        catalog.FittingType(hull) is { } type
        && MilitiaComplexRules.CanCapture(type.Id, type.GroupId)
        && MissionGateAccessRules.AllowsShip(access, type.Id, type.GroupId, type.CategoryId, 0)
        && !fitted.Any(item => ProhibitsEquipment(catalog, access, item));

    private static bool ProhibitsEquipment(CatalogDbContext catalog, MissionGateAccess access, int typeId)
    {
        var fitted = catalog.RequireItem(typeId);
        return access.ForbiddenEquipment!.Contains(fitted.Id, fitted.GroupId, fitted.CategoryId);
    }
}
