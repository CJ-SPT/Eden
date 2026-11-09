using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Mining;
using Eden.World.Storage;

namespace Eden.World.Combat.Npcs;

internal static class NpcHostility
{
    // Call only after the owning offensive operation has validated and paid its
    // costs. The caller's transaction includes group alerting and damage/launch.
    internal static void Record(WorldDbContext world, CatalogDbContext catalog, WorldNpc attacked, long shipId, long now)
    {
        if (attacked.Role == NpcRole.Concord)
        {
            ConcordPersistence.Attack(world, catalog, attacked, shipId, now);
            return;
        }
        if (NpcPopulationRules.IsMining(attacked.Role))
        {
            NpcMiningSeeder.Alert(world, attacked, shipId, now);
            return;
        }
        if (attacked.Role is NpcRole.Convoy or NpcRole.MissionStructure)
        {
            return;
        }
        var members =
            attacked.Role == NpcRole.Pirate || attacked.EncounterId == 0
                ? [attacked]
                : world
                    .Set<WorldNpc>()
                    .Where(n =>
                        n.EncounterId == attacked.EncounterId
                        && n.SystemId == attacked.SystemId
                        && n.Role != NpcRole.Pirate
                        && n.DestroyedAt == null
                        && n.RetiredAt == null
                    )
                    .OrderBy(n => n.Id)
                    .ToArray();
        foreach (var member in members)
        {
            if (member.DestroyedAt.HasValue || member.RetiredAt.HasValue || world.Set<NpcAggression>().Find(member.Id) is not null)
            {
                continue;
            }
            var attributes = PilotFittingStore.ReadAttributes(catalog, member.TypeId);
            double delay = attributes.GetValueOrDefault(
                DogmaAttributeId.EntityAttackDelayMinimum,
                member.Role == NpcRole.Pirate ? 0 : NpcGuardRules.ReactionMilliseconds
            );
            world.Add(NpcAggression.Start(member.Id, shipId, delay, now));
        }
    }
}
