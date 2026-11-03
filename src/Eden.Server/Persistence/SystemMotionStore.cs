using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed partial class SqliteWorld
{
    private static readonly bool TraceMotion = Environment.GetEnvironmentVariable("EDEN_TRACE_MOTION") == "1";

    public IReadOnlyList<ShipMotionResult> Advance(
        int systemId,
        int stamp,
        long now,
        IReadOnlyList<ShipMotion> ships,
        IReadOnlyList<StaticSpaceObject> objects
    )
    {
        lock (_database.MotionLock)
        {
            return AdvanceSystem(systemId, stamp, now, ships, objects);
        }
    }

    private IReadOnlyList<ShipMotionResult> AdvanceSystem(
        int systemId,
        int stamp,
        long now,
        IReadOnlyList<ShipMotion> ships,
        IReadOnlyList<StaticSpaceObject> objects
    )
    {
        using WorldDbContext world = new(_database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(_database.CatalogPath);
        var npcs = world.Set<WorldNpc>().Where(n => n.SystemId == systemId && n.DestroyedAt == null).OrderBy(n => n.Id).ToArray();
        var starts = npcs.ToDictionary(n => n.Id, NpcMotion.Position);
        var velocities = npcs.ToDictionary(n => n.Id, NpcMotion.Velocity);
        var types = npcs.Select(n => n.TypeId).Distinct().ToDictionary(id => id, id => catalog.Set<ItemType>().Single(t => t.Id == id));
        var attributes = types.Keys.ToDictionary(id => id, id => PilotFittingStore.ReadAttributes(catalog, id));
        var hulls = npcs.ToDictionary(
            n => n.Id,
            n => new FlightProperties(
                0,
                (float)(types[n.TypeId].Radius ?? 0),
                types[n.TypeId].Mass ?? 0,
                NpcMotion.Agility(attributes[n.TypeId]),
                (float)
                    PilotTackleStore
                        .NpcAttributes(world, n.Id, attributes[n.TypeId])
                        .GetValueOrDefault(Eden.Gameplay.Dogma.DogmaAttributeId.MaxVelocity),
                0
            )
        );
        var available = ships.Where(s => s.Solid && s.Navigation.Warp is null && s.Navigation.WarpDestination is null);
        var targets = available.ToDictionary(s => s.Id, s => new StaticSpaceObject(s.Id, 0, 0, 0, 0, s.Position, s.Hull.ShipRadius));
        foreach (var npc in npcs)
        {
            targets.Add(npc.Id, new(npc.Id, npc.TypeId, 0, 0, 0, starts[npc.Id], hulls[npc.Id].ShipRadius));
        }
        var obstacles = objects.Where(o => o.GroupId != AsteroidDefinitions.BeltGroup).ToArray();
        List<ShipMotionResult> players = [];
        List<CollisionMotion> bodies = [];
        foreach (var ship in ships)
        {
            var navigation = ship.Navigation.Copy();
            var tick = navigation.Advance(
                ship.Id,
                stamp,
                ship.Hull,
                ship.Position,
                id =>
                    targets.GetValueOrDefault(id)
                    ?? (ship.Target?.Id == id && ships.All(s => s.Id != id) ? ship.Target : null)
                    ?? objects.FirstOrDefault(o => o.Id == id),
                obstacles
            );
            bodies.Add(
                new(
                    ship.Id,
                    ship.Hull,
                    ship.Position,
                    tick.InitialVelocity,
                    tick.Position,
                    navigation.Velocity,
                    ship.Solid && ship.Navigation.Warp is null && navigation.Warp is null
                )
            );
            players.Add(new(ship with { Stamp = stamp, Position = tick.Position, Navigation = navigation }, tick.Changes));
        }
        foreach (var npc in npcs)
        {
            var type = types[npc.TypeId];
            var effective = PilotTackleStore.NpcAttributes(world, npc.Id, attributes[npc.TypeId]);
            var aggression = world.Set<NpcAggression>().Find(npc.Id);
            var initialVelocity = velocities[npc.Id];
            if (
                aggression is not null
                && targets.TryGetValue(aggression.TargetShipId, out var target)
                && (NpcMotion.Engaged(npc) || now >= aggression.ReadyAt)
            )
            {
                NpcMotion.Pursue(npc, target.Position, target.Radius, type, effective, now, stamp);
            }
            else if (npc.Navigation == NpcNavigationState.Returning || NpcMotion.Engaged(npc))
            {
                NpcMotion.Return(npc);
                NpcMotion.Advance(
                    npc,
                    NpcMotion.Home(catalog.Set<Celestial>().Single(b => b.Id == npc.BeltId)),
                    type,
                    effective,
                    now,
                    returning: true
                );
            }
            else if (npc.MotionAt / TimeSpan.TicksPerSecond < now / TimeSpan.TicksPerSecond)
            {
                initialVelocity = NpcMotionRules.CoastVelocity(initialVelocity);
                var coast = NpcMotionRules.Coast(
                    new(npc.Id, starts[npc.Id], velocities[npc.Id], npc.Navigation),
                    hulls[npc.Id].InertiaTime
                );
                (npc.X, npc.Y, npc.Z) = (coast.Position.X, coast.Position.Y, coast.Position.Z);
                (npc.VelocityX, npc.VelocityY, npc.VelocityZ) = (coast.Velocity.X, coast.Velocity.Y, coast.Velocity.Z);
                npc.MotionAt = now;
            }
            bodies.Add(new(npc.Id, hulls[npc.Id], starts[npc.Id], initialVelocity, NpcMotion.Position(npc), NpcMotion.Velocity(npc)));
        }
        var resolved = ShipCollisions.Resolve(bodies).ToDictionary(b => b.Id);
        for (int index = 0; index < players.Count; index++)
        {
            var result = players[index];
            var collision = resolved[result.Ship.Id];
            result.Ship.Navigation.SetVelocity(collision.Velocity);
            players[index] = result with { Ship = result.Ship with { Position = collision.Position }, Bumped = collision.Bumped };
            var saved =
                world.Positions.Find(result.Ship.Id)
                ?? world.Positions.Add(new FlightPosition { ShipId = result.Ship.Id, SystemId = systemId }).Entity;
            (saved.X, saved.Y, saved.Z) = (collision.Position.X, collision.Position.Y, collision.Position.Z);
        }
        foreach (var npc in npcs)
        {
            var collision = resolved[npc.Id];
            (npc.X, npc.Y, npc.Z) = (collision.Position.X, collision.Position.Y, collision.Position.Z);
            (npc.VelocityX, npc.VelocityY, npc.VelocityZ) = (collision.Velocity.X, collision.Velocity.Y, collision.Velocity.Z);
        }
        world.SaveChanges();
        transaction.Commit();
        _database.MotionStamps[systemId] = stamp;
        foreach (var collision in resolved.Values.Where(c => c.Bumped))
        {
            _database.CollisionStamps[collision.Id] = stamp;
        }
        if (TraceMotion)
        {
            var nearby = bodies.Where(b => ships.Any(s => s.Id == b.Id)).ToArray();
            foreach (var ship in nearby)
            {
                foreach (var other in bodies.Where(b => b.Id > ship.Id))
                {
                    var first = resolved[ship.Id];
                    var second = resolved[other.Id];
                    double range = Math.Max(0, first.Position.DistanceTo(second.Position) - ship.Hull.ShipRadius - other.Hull.ShipRadius);
                    if (range <= NpcEncounterDefinitions.VisibilityRange)
                    {
                        Console.WriteLine(
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Motion system={systemId} tick={stamp} ships={ship.Id},{other.Id} range={range:F3}m"
                            )
                        );
                    }
                }
            }
        }
        return players;
    }
}
