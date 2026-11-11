using Eden.Data.World.Drones;
using Eden.Gameplay.Drones;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

internal static partial class DroneCombatPersistence
{
    internal static DroneCombatTriggers Read(WorldAction world, long shipId) =>
        world.Set<WorldDroneCombatTriggers>().Find(shipId) is { } saved ? Read(saved) : DroneCombatTriggers.Empty(shipId);

    internal static void Module(WorldAction world, long shipId, long targetId, bool playerTarget, long now)
    {
        var row = Row(world, shipId);
        Write(row, Read(row).Module(targetId, now));
        if (playerTarget)
        {
            Incoming(world, targetId, shipId, now);
        }
    }

    internal static void Incoming(WorldAction world, long shipId, long sourceId, long now)
    {
        var row = Row(world, shipId);
        Write(row, Read(row).Attacked(sourceId, now));
    }

    private static WorldDroneCombatTriggers Row(WorldAction world, long shipId) =>
        world.Set<WorldDroneCombatTriggers>().Find(shipId) ?? world.Add(new WorldDroneCombatTriggers { ShipId = shipId }).Entity;
}
