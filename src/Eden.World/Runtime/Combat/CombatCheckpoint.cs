using System.Text.Json;
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
    IReadOnlyList<DronePilotHostility> DroneHostilities,
    IReadOnlyList<DroneCombatTriggers> DroneTriggers
)
{
    internal bool HasChanges =>
        Ships.Count
            + Npcs.Count
            + Positions.Count
            + Drones.Count
            + Aggressions.Count
            + DroneTimers.Count
            + DroneEngagements.Count
            + DroneHostilities.Count
            + DroneTriggers.Count
        > 0;

    private const int PositionBatchSize = 128;
    private static readonly JsonSerializerOptions PositionOptions = new() { IgnoreReadOnlyProperties = true };

    internal void Apply(WorldDbContext world)
    {
        using var phase = SessionPerformance.Phase("combat.project");
        using var transaction = world.Database.CurrentTransaction is null ? world.Database.BeginTransaction() : null;
        foreach (var batch in Positions.Chunk(PositionBatchSize))
        {
            // A traffic mutation projects all pending resident motion before reading SQL.
            // Bound command preparation by batches instead of issuing one command per traveler.
            string positions = JsonSerializer.Serialize(batch, PositionOptions);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPosition(ShipId,SystemId,X,Y,Z)
                SELECT json_extract(value,'$.ShipId'),json_extract(value,'$.SystemId'),
                    json_extract(value,'$.Position.X'),json_extract(value,'$.Position.Y'),json_extract(value,'$.Position.Z')
                FROM json_each({positions}) WHERE 1
                ON CONFLICT(ShipId) DO UPDATE SET X=excluded.X,Y=excluded.Y,Z=excluded.Z
                """
            );
        }
        LiveCombatRuntime.ProjectDrones(world, Drones);
        LiveCombatRuntime.ProjectDroneTriggers(world, DroneTriggers);
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
        transaction?.Commit();
    }
}

internal sealed partial class LiveCombatRuntime
{
    internal bool Participates(TransactionParticipants participants, long pilot, long ship, int? system = null) =>
        participants.Includes(pilot, ship, system ?? _positions.GetValueOrDefault(ship)?.System)
        || _shipPilots.TryGetValue(ship, out long ownerPilot)
            && (
                participants.Pilots.Contains(ownerPilot)
                || _ships.TryGetValue(ownerPilot, out var ownerShip) && participants.Systems.Contains(ownerShip.SystemId)
            )
        || _ships.TryGetValue(pilot, out var state) && participants.Includes(pilot, state.ShipId, state.SystemId);

    internal CombatCheckpoint CaptureCheckpoint(TransactionParticipants? participants = null)
    {
        var own = CaptureOwnCheckpoint(participants);
        return systemId is null && owner.CurrentSystem is null ? owner.CaptureSystemCombat(own, participants) : own;
    }

    private CombatCheckpoint CaptureOwnCheckpoint(TransactionParticipants? participants) =>
        new(
            _dirtyShips
                .Where(id => participants is null || Participates(participants, id, _ships[id].ShipId, _ships[id].SystemId))
                .Select(id => new CombatShipChange(_savedShips[id], _ships[id]))
                .ToArray(),
            _dirtyNpcs
                .Where(id =>
                    participants is null
                    || participants.Systems.Contains(_npcs[id].SystemId)
                    || participants.Includes(SharedSimulationResource.Npc, id)
                )
                .Select(id => new CombatNpcChange(_savedNpcs[id], _npcs[id]))
                .ToArray(),
            _dirtyPositions
                .Where(id => participants is null || Participates(participants, 0, id))
                .Select(id => new CombatPosition(id, _positions[id].System, _positions[id].Position))
                .ToArray(),
            _dirtyDrones
                .Where(id =>
                    participants is null
                    || Participates(
                        participants,
                        _liveDrones[id].Contact.PilotId,
                        _liveDrones[id].Contact.ShipId,
                        _liveDrones[id].Contact.SystemId
                    )
                )
                .Select(id => _liveDrones[id])
                .ToArray(),
            _dirtyAggressions
                .Where(id =>
                    participants is null
                    || Participates(participants, 0, _aggressions![id].ShipId)
                    || participants.Includes(SharedSimulationResource.Npc, id)
                    || _npcs.TryGetValue(id, out var npc) && participants.Systems.Contains(npc.SystemId)
                )
                .Select(id => _aggressions![id])
                .ToArray(),
            _droneTimers.Values.Where(t => participants is null || Participates(participants, t.PilotId, 0, t.SystemId)).ToArray(),
            _droneEngagements
                .Values.Where(e =>
                    participants is null || Participates(participants, e.LowerPilotId, 0) || Participates(participants, e.HigherPilotId, 0)
                )
                .ToArray(),
            _droneHostilities
                .Values.Where(h =>
                    participants is null || Participates(participants, 0, h.SourceShipId) || Participates(participants, 0, h.TargetShipId)
                )
                .ToArray(),
            _dirtyDroneCombatTriggers
                .Where(id => participants is null || Participates(participants, 0, id))
                .Select(id => _droneCombatTriggers[id])
                .ToArray()
        );
}
