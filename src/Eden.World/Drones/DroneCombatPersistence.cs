using Eden.Data.World.Drones;
using Eden.Gameplay.Drones;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

internal static partial class DroneCombatPersistence
{
    internal static DroneCombatTriggers Read(WorldDbContext world, long shipId) =>
        world.Set<WorldDroneCombatTriggers>().Find(shipId) is { } saved ? Read(saved) : DroneCombatTriggers.Empty(shipId);

    internal static DroneCombatTriggers Read(WorldDroneCombatTriggers saved) =>
        new(
            saved.ShipId,
            new(saved.OffensiveRevision, saved.OffensiveTargetId, saved.OffensiveAt),
            new(saved.IncomingRevision, saved.IncomingSourceId, saved.IncomingAt)
        );

    // These updates share the caller's payment/effect transaction. Recording an
    // incoming attack never records an outgoing fitted-module action in turn.
    internal static void Module(WorldDbContext world, long shipId, long targetId, bool playerTarget, long now)
    {
        var row = Row(world, shipId);
        Write(row, Read(row).Module(targetId, now));
        if (playerTarget)
        {
            Incoming(world, targetId, shipId, now);
        }
    }

    internal static void Incoming(WorldDbContext world, long shipId, long sourceId, long now)
    {
        var row = Row(world, shipId);
        Write(row, Read(row).Attacked(sourceId, now));
    }

    private static WorldDroneCombatTriggers Row(WorldDbContext world, long shipId) =>
        world.Set<WorldDroneCombatTriggers>().Find(shipId) ?? world.Add(new WorldDroneCombatTriggers { ShipId = shipId }).Entity;

    private static void Write(WorldDroneCombatTriggers row, DroneCombatTriggers value)
    {
        row.OffensiveRevision = value.Offensive.Revision;
        row.OffensiveTargetId = value.Offensive.OtherId;
        row.OffensiveAt = value.Offensive.At;
        row.IncomingRevision = value.Incoming.Revision;
        row.IncomingSourceId = value.Incoming.OtherId;
        row.IncomingAt = value.Incoming.At;
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldDroneCombatTriggers(
                ShipId INTEGER PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                OffensiveRevision INTEGER NOT NULL,OffensiveTargetId INTEGER NOT NULL,OffensiveAt INTEGER NOT NULL,
                IncomingRevision INTEGER NOT NULL,IncomingSourceId INTEGER NOT NULL,IncomingAt INTEGER NOT NULL);
            """
        );
}
