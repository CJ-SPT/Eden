using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Insurance;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Weapons;
using Eden.World.Docking;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Propulsion;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal sealed partial class NpcCombatStore
{
    internal static void Disengage(WorldAction world, long shipId, long? npcId = null)
    {
        var aggression = world
            .Set<NpcAggression>()
            .Where(a =>
                a.TargetShipId == shipId
                && (npcId == null || a.NpcId == npcId)
                && !world.Set<WorldNpc>().Any(n => n.Id == a.NpcId && n.Role == NpcRole.Concord)
            )
            .ToArray();
        var ids = aggression.Select(a => a.NpcId).ToArray();
        foreach (
            var npc in world.Set<WorldNpc>().Where(n => Enumerable.Contains(ids, n.Id) && n.DestroyedAt == null && n.RetiredAt == null)
        )
        {
            NpcMotion.Return(npc);
        }
        world.RemoveRange(aggression);
        foreach (
            var missile in world
                .Set<WorldMissile>()
                .Where(m =>
                    m.SourceKind == MissileSourceKind.Npc
                    && m.TargetId == shipId
                    && (npcId == null || m.SourceShipId == npcId)
                    && m.Status == MissileFlightStatus.Flying
                )
        )
        {
            missile.Status = MissileFlightStatus.Expired;
            missile.EndedAt = missile.UpdatedAt;
        }
        world.SaveChanges();
    }
}
