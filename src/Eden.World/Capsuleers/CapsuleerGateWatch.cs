using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal sealed record GateWatcherPosition(int? SystemId, int StationId, SpacePosition Position);

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerGateWatch(WorldDatabase database) : ICapsuleerGateWatch
{
    private sealed record GateGrid(WatchedTrafficGate Gate, SpacePosition Position, double Radius);

    private static readonly CatalogDefinition<GateGrid[]> s_grids = new("capsuleer-watched-gates", Grids);

    public IReadOnlyList<WatchedTrafficGate> Read(HumanActivitySnapshot activity)
    {
        return LiveWorld.Execute(database.Path, () => ReadOwned(activity), nameof(Read));
    }

    private IReadOnlyList<WatchedTrafficGate> ReadOwned(HumanActivitySnapshot activity)
    {
        if (activity.Pilots.Count == 0)
        {
            return [];
        }
        var live = LiveWorld.Find(database.Path);
        using var owner = live?.Enter();
        List<WatchedTrafficGate>? watched = null;
        foreach (var pilot in activity.Pilots)
        {
            long ship = live?.Reads is { } ships
                ? ships.GateWatcherShip(pilot.PilotId, () => ActiveShip(pilot.PilotId))
                : ActiveShip(pilot.PilotId);
            if (ship == 0)
            {
                continue;
            }
            var baseline = live?.Reads is { } positions ? positions.GateWatcherPosition(ship, () => Position(ship)) : Position(ship);
            if (baseline is null)
            {
                continue;
            }
            var catalog = database.Catalog;
            if ((baseline.SystemId ?? catalog.FindStation(baseline.StationId)?.SolarSystemId) != pilot.SystemId)
            {
                continue;
            }
            var position = live?.Combat?.InformationPosition(ship) ?? baseline.Position;
            GateGrid? closest = null;
            double clearance = CapsuleerEncounterRules.GateWatchDistanceMeters;
            foreach (var grid in catalog.Definition(s_grids, pilot.SystemId))
            {
                double distance = position.DistanceTo(grid.Position) - grid.Radius;
                if (distance <= clearance)
                {
                    closest = grid;
                    clearance = distance;
                }
            }
            if (closest is not null)
            {
                watched ??= [];
                if (!watched.Contains(closest.Gate))
                {
                    watched.Add(closest.Gate);
                }
            }
        }
        return watched is null ? [] : watched;
    }

    private long ActiveShip(long pilot)
    {
        using WorldDbContext world = new(database.Path) { ReadsDirectionalPositions = true };
        return world.Pilots.Where(p => p.Id == pilot && p.AccountId > 0).Select(p => p.ActiveShipId).FirstOrDefault();
    }

    private GateWatcherPosition? Position(long ship)
    {
        using WorldDbContext world = new(database.Path) { ReadsDirectionalPositions = true };
        return (
            from row in world.Positions
            join hull in world.Ships on row.ShipId equals hull.Id
            where row.ShipId == ship
            select new GateWatcherPosition(row.SystemId, hull.StationId, new(row.X, row.Y, row.Z))
        ).SingleOrDefault();
    }

    private static GateGrid[] Grids(CatalogDbContext catalog, int system)
    {
        var gates = catalog.Definitions.Gates;
        return SystemObjects
            .Geometry(catalog, system)
            .Where(o => o.DestinationSystemId != system && o.DestinationSystemId is not null && o.DestinationGateId is not null)
            .Where(o => gates.TryGetValue(checked((int)o.DestinationGateId!.Value), out var other) && other.DestinationId == o.Id)
            .Select(o => new GateGrid(new(system, o.DestinationSystemId!.Value, o.Id, o.DestinationGateId!.Value), o.Position, o.Radius))
            .OrderBy(g => g.Gate.ArrivalGateId)
            .ToArray();
    }
}
