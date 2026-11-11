using Eden.Gameplay.Drones;
using Eden.World.Drones;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private readonly ResidentMap<long, DroneCombatTriggers> _droneCombatTriggers = [];
    private readonly ResidentSet<long> _dirtyDroneCombatTriggers = [];

    internal DroneCombatTriggers DroneTriggers(long shipId)
    {
        if (
            systemId is null
            && owner.CurrentSystem is null
            && owner.CombatForShip(shipId) is { } resident
            && !ReferenceEquals(resident, this)
        )
        {
            return resident.DroneTriggers(shipId);
        }
        return Execute(() => DroneTriggersOwned(shipId), nameof(DroneTriggers));
    }

    private DroneCombatTriggers DroneTriggersOwned(long shipId)
    {
        using var ownership = owner.Enter();
        if (!_droneCombatTriggers.TryGetValue(shipId, out var value))
        {
            value = Loading(() =>
            {
                if (owner.SqlConnections == 0 && owner.Economy.IsLoaded)
                {
                    return owner.Economy.Inspect(world => DroneCombatPersistence.Read(world, shipId), new(ships: [shipId]));
                }
                using WorldDbContext world = new(database.Path);
                return DroneCombatPersistence.Read(world, shipId);
            });
            _droneCombatTriggers[shipId] = value;
        }
        return value;
    }

    private void RecordDroneModule(long shipId, long targetId, long now)
    {
        _droneCombatTriggers[shipId] = DroneTriggers(shipId).Module(targetId, now);
        _dirtyDroneCombatTriggers.Add(shipId);
    }

    private void RecordDroneIncoming(long shipId, long sourceId, long now)
    {
        _droneCombatTriggers[shipId] = DroneTriggers(shipId).Attacked(sourceId, now);
        _dirtyDroneCombatTriggers.Add(shipId);
    }

    internal static void ProjectDroneTriggers(WorldDbContext world, IReadOnlyList<DroneCombatTriggers> values)
    {
        foreach (var value in values)
        {
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldDroneCombatTriggers(ShipId,OffensiveRevision,OffensiveTargetId,OffensiveAt,
                    IncomingRevision,IncomingSourceId,IncomingAt)
                VALUES({value.ShipId},{value.Offensive.Revision},{value.Offensive.OtherId},{value.Offensive.At},
                    {value.Incoming.Revision},{value.Incoming.OtherId},{value.Incoming.At})
                ON CONFLICT(ShipId) DO UPDATE SET OffensiveRevision=excluded.OffensiveRevision,
                    OffensiveTargetId=excluded.OffensiveTargetId,OffensiveAt=excluded.OffensiveAt,
                    IncomingRevision=excluded.IncomingRevision,IncomingSourceId=excluded.IncomingSourceId,
                    IncomingAt=excluded.IncomingAt
                """
            );
        }
    }
}
