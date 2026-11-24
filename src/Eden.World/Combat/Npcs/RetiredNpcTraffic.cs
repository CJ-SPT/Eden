using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Loot;
using Eden.World.Runtime.Economy;

namespace Eden.World.Combat.Npcs;

internal static class RetiredNpcTraffic
{
    private const int MaximumRemovalsPerSystem = 64;

    // Keep recent departure identities for scene updates and outstanding combat.
    internal static long Cutoff(long now) => now - WreckDefinitions.LifetimeSeconds * TimeSpan.TicksPerSecond;

    internal static void Prune(WorldAction world, int system, long now)
    {
        if (world is not EconomyTransaction)
        {
            return;
        }
        long cutoff = Cutoff(now);
        var candidates = NpcPopulationMember
            .InSystem(world, system)
            .Where(npc =>
                NpcPopulationRules.IsIncursionPatrol(npc.AnchorKind) && (npc.RetiredAt ?? npc.DestroyedAt) <= cutoff
                || npc.DestroyedAt is null
                    && npc.RetiredAt <= cutoff
                    && (
                        npc is { Role: NpcRole.Convoy, AnchorKind: NpcAnchorKind.Station }
                        || npc is { Role: NpcRole.MiningHauler or NpcRole.Pirate, AnchorKind: NpcAnchorKind.Belt }
                    )
            )
            .Select(npc => npc.Id)
            .Where(id => !Referenced(world, id))
            .Take(MaximumRemovalsPerSystem)
            .ToArray();
        var npcs = world.Set<WorldNpc>();
        foreach (long id in candidates)
        {
            npcs.Remove(npcs.Find(id)!);
        }
    }

    private static bool Referenced(WorldAction world, long id) =>
        world.Set<NpcAggression>().Find(id) is not null
        || world.Set<NpcBounty>().Any(bounty => bounty.NpcId == id)
        || world.Set<ShipLoss>().Any(loss => loss.NpcId == id)
        || world.Set<WorldWreck>().Any(wreck => wreck.NpcId == id)
        || world.Set<SavedCombatDamage>().Any(damage => damage.VictimId == id || damage.AttackerId == id)
        || world.Set<WorldMissile>().Any(missile => missile.SourceShipId == id && missile.SourceKind == MissileSourceKind.Npc)
        || world.Set<WorldMissile>().Any(missile => missile.TargetId == id && missile.TargetKind == MissileTargetKind.Npc);
}
