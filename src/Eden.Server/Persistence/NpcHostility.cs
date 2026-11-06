using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal static class NpcHostility
{
    // Call only after the owning offensive operation has validated and paid its
    // costs. The caller's transaction includes group alerting and damage/launch.
    internal static void Record(WorldDbContext world, CatalogDbContext catalog, WorldNpc attacked, long shipId, long now)
    {
        if (attacked.Role == NpcRole.Convoy)
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
