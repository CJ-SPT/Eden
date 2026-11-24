using Eden.Gameplay.Combat;
using Eden.World.Combat.Npcs;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class RetiredNpcTrafficMigration
{
    internal const int Version = 222;

    internal static void RemoveCompletedTraffic(WorldDbContext world, long now)
    {
        world.Database.ExecuteSql(
            $"""
            DELETE FROM WorldNpc
            WHERE DestroyedAt IS NULL AND RetiredAt<={RetiredNpcTraffic.Cutoff(now)}
                AND ((Role={(int)NpcRole.Convoy} AND AnchorKind={(int)NpcAnchorKind.Station})
                    OR (Role={(int)NpcRole.MiningHauler} AND AnchorKind={(int)NpcAnchorKind.Belt}))
                AND Id NOT IN (SELECT NpcId FROM WorldNpcAggression)
                AND Id NOT IN (SELECT NpcId FROM WorldNpcBounty)
                AND Id NOT IN (SELECT NpcId FROM WorldShipLoss WHERE NpcId IS NOT NULL)
                AND Id NOT IN (SELECT NpcId FROM WorldWreck WHERE NpcId IS NOT NULL)
                AND Id NOT IN (SELECT VictimId FROM WorldCombatDamage)
                AND Id NOT IN (SELECT AttackerId FROM WorldCombatDamage)
                AND Id NOT IN (SELECT SourceShipId FROM WorldMissile WHERE SourceKind={(int)MissileSourceKind.Npc})
                AND Id NOT IN (SELECT TargetId FROM WorldMissile WHERE TargetKind={(int)MissileTargetKind.Npc})
            """
        );
    }
}
