using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed record CombatShipChange(LiveCombatShip Before, LiveCombatShip After);

internal sealed record CombatNpcChange(LiveCombatNpc Before, LiveCombatNpc After);

internal sealed record CombatPosition(long ShipId, int? SystemId, SpacePosition Position);

// Captured under the owner. Simulation replaces these records and their collection
// values, so projection can read the captured generation after releasing the owner.
internal sealed record CombatCheckpoint(
    IReadOnlyList<CombatShipChange> Ships,
    IReadOnlyList<CombatNpcChange> Npcs,
    IReadOnlyList<CombatPosition> Positions,
    IReadOnlyList<LiveDrone> Drones,
    IReadOnlyList<LiveAggression> Aggressions,
    IReadOnlyList<DronePilotTimers> DroneTimers,
    IReadOnlyList<DronePilotEngagement> DroneEngagements,
    IReadOnlyList<DronePilotHostility> DroneHostilities
)
{
    internal void Apply(WorldDbContext world)
    {
        using var phase = SessionPerformance.Phase("combat.project");
        using var transaction = world.Database.BeginTransaction();
        foreach (var position in Positions)
        {
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPosition(ShipId,SystemId,X,Y,Z)
                VALUES({position.ShipId},{position.SystemId},{position.Position.X},{position.Position.Y},{position.Position.Z})
                ON CONFLICT(ShipId) DO UPDATE SET X=excluded.X,Y=excluded.Y,Z=excluded.Z
                """
            );
        }
        LiveCombatRuntime.ProjectDrones(world, Drones);
        LiveCombatRuntime.ProjectDronePlayers(world, DroneTimers, DroneEngagements, DroneHostilities);
        foreach (var ship in Ships)
        {
            LiveCombatRuntime.ProjectShip(world, ship.Before, ship.After);
        }
        foreach (var npc in Npcs)
        {
            LiveCombatRuntime.ProjectNpc(world, npc.Before, npc.After);
        }
        foreach (var aggression in Aggressions)
        {
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldNpcAggression(NpcId,TargetShipId,ReadyAt,MissileReadyAt,LastShotAt,LastTargetId)
                VALUES({aggression.NpcId},{aggression.ShipId},{aggression.ReadyAt},{aggression.MissileReadyAt},
                    {aggression.LastShotAt},{aggression.LastTargetId})
                ON CONFLICT(NpcId) DO UPDATE SET TargetShipId=excluded.TargetShipId,ReadyAt=excluded.ReadyAt,
                    MissileReadyAt=excluded.MissileReadyAt,LastShotAt=excluded.LastShotAt,LastTargetId=excluded.LastTargetId
                """
            );
        }
        world.SaveChanges();
        transaction.Commit();
    }
}

internal sealed partial class LiveCombatRuntime
{
    internal CombatCheckpoint CaptureCheckpoint() =>
        new(
            _dirtyShips.Select(id => new CombatShipChange(_savedShips[id], _ships[id])).ToArray(),
            _dirtyNpcs.Select(id => new CombatNpcChange(_savedNpcs[id], _npcs[id])).ToArray(),
            _dirtyPositions.Select(id => new CombatPosition(id, _positions[id].System, _positions[id].Position)).ToArray(),
            _dirtyDrones.Select(id => _liveDrones[id]).ToArray(),
            _dirtyAggressions.Select(id => _aggressions![id]).ToArray(),
            _droneTimers.Values.ToArray(),
            _droneEngagements.Values.ToArray(),
            _droneHostilities.Values.ToArray()
        );
}
