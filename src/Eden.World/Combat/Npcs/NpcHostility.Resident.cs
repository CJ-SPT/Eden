using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Mining;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat.Npcs;

internal static partial class NpcHostility
{
    internal static IEnumerable<long> Participants(WorldAction world, long target)
    {
        yield return target;
        if (world.Set<WorldNpc>().Find(target) is { EncounterId: not 0, Role: not NpcRole.Pirate } npc)
        {
            foreach (
                long id in world
                    .Set<WorldNpc>()
                    .Where(member =>
                        member.EncounterId == npc.EncounterId
                        && member.SystemId == npc.SystemId
                        && member.Id != target
                        && member.DestroyedAt == null
                        && member.RetiredAt == null
                    )
                    .Select(member => member.Id)
            )
            {
                yield return id;
            }
        }
    }

    internal static void Record(WorldAction world, CatalogDbContext catalog, WorldNpc attacked, long shipId, long now)
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
