using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

[Injectable]
internal sealed partial class AssetSafetyStore(WorldDatabase database, TimeProvider clock) : IPilotAssetSafety
{
    private static readonly CatalogDefinition<AssetSafetyDestination> s_nearestStations = new("asset-safety-station", LoadNearestStation);

    private sealed record StationDistance(int Station, double Distance);

    public bool Available => LiveWorld.Find(database.Path) is not null;

    public long Revision(long ownerId) => LiveWorld.Find(database.Path)?.Economy.AssetSafetyRevision(ownerId) ?? 0;

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private T Execute<T>(
        long pilot,
        Func<WorldAction, CatalogDbContext, long, T> operation,
        int? system = null,
        bool prepareStation = false
    )
    {
        if (!Available)
        {
            throw new InvalidOperationException("Asset safety requires the resident world.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (prepareStation && system is int target && SafetyEnabled(catalog, target))
        {
            _ = NearestStation(catalog, target);
        }
        using WorldActionScope scope = new(
            database,
            select: world =>
            {
                var selected = world.Pilots.Find(pilot);
                long corporation = selected?.CorporationId ?? 0;
                int? dockedSystem = selected?.ActiveShip.StructureId is long docked ? world.Structure(docked)?.SystemId : null;
                var wraps = world.Filter<SavedAssetSafety>(w => w.OwnerId == pilot || w.OwnerId == corporation).ToArray();
                return new(
                    pilots: [pilot],
                    systems: wraps
                        .Select(w => w.SystemId)
                        .Concat(wraps.Select(w => catalog.RequireStation(w.NearestStationId).SolarSystemId))
                        .Concat(system is int requested ? [requested] : [])
                        .Concat(dockedSystem is int local ? [local] : [])
                        .Distinct()
                );
            }
        );
        long now = Now;
        if (system is int requestedSystem)
        {
            StructureMissileSettlement.Advance(scope.World, catalog, requestedSystem, now);
        }
        if (
            scope.World.Pilots.Find(pilot)?.ActiveShip.StructureId is long docked
            && scope.World.Structure(docked) is { } structure
            && structure.SystemId != system
        )
        {
            StructureMissileSettlement.Advance(scope.World, catalog, structure.SystemId, now);
        }
        var result = operation(scope.World, catalog, now);
        scope.Commit();
        return result;
    }

    private static long Owner(WorldAction world, long pilot, bool corporation)
    {
        if (world.Pilots.Find(pilot) is null)
        {
            throw new InvalidDataException("Choose a selected pilot.");
        }
        if (!corporation)
        {
            return pilot;
        }
        var corp = CorporationRoleStore.Current(world, pilot);
        if (!CorporationRoleStore.Effective(world, pilot, corp).Roles.Has(CorporationRole.Director))
        {
            throw new InvalidDataException("Only the CEO or a Director can manage corporation asset safety.");
        }
        return corp.Id;
    }

    private static SavedAssetSafety Authorize(WorldAction world, long pilot, long id)
    {
        var wrap = world.Set<SavedAssetSafety>().Find(id) ?? throw new InvalidDataException("The asset safety wrap is unavailable.");
        if (wrap.OwnerId != pilot && wrap.OwnerId != Owner(world, pilot, corporation: true))
        {
            throw new InvalidDataException("The asset safety wrap belongs to another owner.");
        }
        return wrap;
    }

    internal static bool SafetyEnabled(CatalogDbContext catalog, int system) =>
        !WormholeRules.IsWormholeSystem(system) && catalog.RequireSystem(system).RegionId != HomeStationRules.PochvenRegion;

    public bool Handles(long inventoryId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return false;
        }
        return live.Economy.Inspect(world => Ancestor(world, inventoryId) is not null);
    }

    internal static SavedAssetSafety? Ancestor(WorldAction world, long id)
    {
        HashSet<long> visited = [];
        while (id != 0 && visited.Add(id))
        {
            if (world.Set<SavedAssetSafety>().Find(id) is { } wrap)
            {
                return wrap;
            }
            if (world.Set<OwnedItem>().Find(id) is { } item)
            {
                id = item.LocationId;
            }
            else if (world.Ships.Find(id)?.StoredInId is long parent)
            {
                id = parent;
            }
            else
            {
                break;
            }
        }
        return null;
    }

    public IReadOnlyList<AssetSafetyWrap> List(long pilotId, bool corporation) =>
        Execute(
            pilotId,
            (world, catalog, now) =>
            {
                long owner = Owner(world, pilotId, corporation);
                Advance(world, catalog, owner, now);
                return world
                    .Filter<SavedAssetSafety>(w => w.OwnerId == owner && w.DeliveredAt == null)
                    .OrderBy(w => w.Id)
                    .AsEnumerable()
                    .Select(w => Snapshot(catalog, w))
                    .ToArray();
            }
        );

    public AssetSafetyWrap? Move(long pilotId, int systemId, long structureId, bool corporation) =>
        Execute(
            pilotId,
            (world, catalog, now) =>
            {
                long owner = Owner(world, pilotId, corporation);
                var structure = PilotStructureStore.Require(world, structureId);
                if (structure.SystemId != systemId || !StructureCatalog.Require(catalog, structure.TypeId).Dockable)
                {
                    throw new InvalidDataException("Choose a dockable structure in the requested system.");
                }
                var ship = world.Pilots.Find(pilotId)!.ActiveShip;
                if (ship.Position is null && ship.StructureId == structureId)
                {
                    throw new InvalidDataException("Leave this structure before moving its assets to safety.");
                }
                var wrap = Collect(world, catalog, structure, owner, now, eject: !SafetyEnabled(catalog, systemId));
                return wrap is null ? null : Snapshot(catalog, wrap);
            },
            systemId,
            prepareStation: true
        );

    public AssetSafetyTargets Destinations(long pilotId, int systemId) =>
        Execute(
            pilotId,
            (world, catalog, now) =>
            {
                long corporation = world.Pilots.Find(pilotId)!.CorporationId;
                var wrap = world
                    .Filter<SavedAssetSafety>(w => w.SystemId == systemId && (w.OwnerId == pilotId || w.OwnerId == corporation))
                    .AsEnumerable()
                    .FirstOrDefault(w => w.OwnerId == pilotId || CanManageCorporation(world, pilotId, w.OwnerId));
                if (wrap is null)
                {
                    return new AssetSafetyTargets([], null);
                }
                var targets = catalog
                    .StationsInSystem(systemId)
                    .Select(s => Station(catalog, s.Id))
                    .Concat(
                        world
                            .Filter<SavedStructure>(s => s.SystemId == systemId && s.State != StructureState.Destroyed)
                            .AsEnumerable()
                            .Where(s =>
                                StructureRules.CanDock(StructureCatalog.Require(catalog, s.TypeId), s.State, 0)
                                && PilotStructureStore.Access(world, s, pilotId)
                            )
                            .Select(s => new AssetSafetyDestination(s.Id, s.TypeId, s.SystemId, s.Name))
                    );
                return new AssetSafetyTargets(targets.OrderBy(s => s.Id).ToArray(), Station(catalog, wrap.NearestStationId));
            },
            systemId
        );

    private static bool CanManageCorporation(WorldAction world, long pilot, long owner) =>
        world.Pilots.Find(pilot)?.CorporationId == owner
        && world.Set<SavedCorporation>().Find(owner) is { } corp
        && CorporationRoleStore.Effective(world, pilot, corp).Roles.Has(CorporationRole.Director);

    public IReadOnlyDictionary<long, string?> Names(long pilotId, IReadOnlyList<long> ids) =>
        Execute(
            pilotId,
            (world, _, _) =>
                ids.Distinct()
                    .ToDictionary(
                        id => id,
                        id =>
                            world.Set<SavedAssetSafety>().Find(id) is { } wrap
                            && (wrap.OwnerId == pilotId || CanManageCorporation(world, pilotId, wrap.OwnerId))
                                ? wrap.Name
                                : null
                    )
        );

    private static AssetSafetyWrap Snapshot(CatalogDbContext catalog, SavedAssetSafety wrap) =>
        new(
            wrap.Id,
            wrap.OwnerId,
            wrap.StructureId,
            wrap.SystemId,
            wrap.Name,
            wrap.CreatedAt,
            checked(wrap.CreatedAt + AssetSafetyRules.ManualDelay),
            checked(wrap.CreatedAt + AssetSafetyRules.AutomaticDelay),
            wrap.DestinationId,
            Station(catalog, wrap.NearestStationId)
        );

    private static AssetSafetyDestination Station(CatalogDbContext catalog, int id)
    {
        var station = catalog.RequireStation(id);
        return new(station.Id, station.TypeId, station.SolarSystemId, catalog.StationName(id));
    }

    internal static void Prepare(CatalogDbContext catalog, int system)
    {
        if (SafetyEnabled(catalog, system))
        {
            _ = NearestStation(catalog, system);
        }
    }

    private static int NearestStation(CatalogDbContext catalog, int system) =>
        checked((int)catalog.Definition(s_nearestStations, system).Id);

    private static AssetSafetyDestination LoadNearestStation(CatalogDbContext catalog, int system)
    {
        double originSecurity = catalog.Definitions.SystemSecurity.GetValueOrDefault(system);
        bool high = originSecurity >= StructureRules.HighSecurityBoundary;
        var stations = catalog
            .Stations()
            .Where(station =>
            {
                double security = catalog.Definitions.SystemSecurity.GetValueOrDefault(station.SolarSystemId);
                return high
                    ? security >= StructureRules.HighSecurityBoundary
                    : security > 0 && security < StructureRules.HighSecurityBoundary;
            })
            .GroupBy(station => station.SolarSystemId)
            .Select(group => group.OrderBy(s => s.Id).First())
            .ToArray();
        var centers = catalog.ReadMany(
            CatalogData.Client.Systems,
            stations.Select(station => (long)station.SolarSystemId).Append(system).Distinct()
        );
        var origin = centers.GetValueOrDefault(system)?.Center;
        SpacePosition? center = origin is { X: { } x, Y: { } y, Z: { } z } ? new(x, y, z) : null;
        var candidates = stations
            .Select(station =>
            {
                var target = centers.GetValueOrDefault(station.SolarSystemId)?.Center;
                double distance =
                    station.SolarSystemId == system ? 0
                    : center is { } point && target is { X: { } x, Y: { } y, Z: { } z } ? point.DistanceTo(new(x, y, z))
                    : double.PositiveInfinity;
                return new StationDistance(station.Id, distance);
            })
            .Where(candidate => double.IsFinite(candidate.Distance))
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Station);
        int nearest =
            candidates.FirstOrDefault()?.Station
            ?? throw new InvalidDataException("The catalog has no eligible NPC station for asset safety recovery.");
        return Station(catalog, nearest);
    }
}
