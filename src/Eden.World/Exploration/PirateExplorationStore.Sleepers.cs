using Eden.Data.Catalog.Exploration;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Exploration;

internal sealed partial class PirateExplorationStore
{
    private TransactionParticipants ExplorationParticipants(WorldAction world, int system, long now)
    {
        var participants = GhostParticipants(world, system, now);
        if (world is not EconomyTransaction || !WormholeRules.IsWormholeSystem(system))
        {
            return participants;
        }
        var pilots = world
            .Filter<SavedHackingAttempt>(a => a.RecoveryPending)
            .AsEnumerable()
            .Where(a =>
                world.Set<SavedHackingContainer>().Find(a.ContainerId) is { } container
                && world.Set<SavedExplorationSite>().Find(container.SiteId)?.SystemId == system
            )
            .Select(a => a.PilotId)
            .ToArray();
        return pilots.Length == 0
            ? participants
            : new(participants.Systems, participants.Pilots.Concat(pilots), participants.Ships, participants.Resources);
    }

    private void RecoverSleeperHacks(
        WorldAction world,
        CatalogDbContext catalog,
        SavedExplorationSite site,
        IReadOnlyList<SavedHackingContainer> containers,
        long now
    )
    {
        foreach (var container in containers)
        {
            var attempt = world.Filter<SavedHackingAttempt>(a => a.ContainerId == container.Id && a.RecoveryPending).SingleOrDefault();
            if (attempt is null)
            {
                continue;
            }
            ReleaseModule(world, attempt);
            world.Remove(attempt);
            if (!container.Hacked && !container.Destroyed)
            {
                FailHack(world, catalog, site, container, now);
            }
            Invalidate(world, site.SystemId);
        }
    }

    private static void GrantSleeperLoot(
        WorldAction world,
        CatalogDbContext catalog,
        SavedHackingContainer container,
        ExplorationDefinition definition,
        long pilot
    )
    {
        if (world is not EconomyTransaction)
        {
            throw new InvalidOperationException("Sleeper rewards require the resident world.");
        }
        var cache = definition.Containers[container.Index];
        foreach (
            var stack in SleeperExplorationLoot.Roll(
                container.LootSeed,
                definition.WormholeClass,
                cache.AnalyzerKind ?? definition.Kind,
                cache.TypeId
            )
        )
        {
            if (catalog.FittingType(stack.TypeId) is null || catalog.Read(CatalogData.Client.Types, (long)stack.TypeId)?.Published != 1)
            {
                throw new InvalidDataException("The pinned Sleeper research reward is unavailable.");
            }
            InventoryPersistence.MergeOutput(world, catalog, pilot, container.Id, (int)InventoryFlag.Cargo, stack.TypeId, stack.Quantity);
        }
    }
}
