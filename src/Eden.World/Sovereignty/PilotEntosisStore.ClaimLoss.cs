using Eden.Data.World.Alliances;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;

namespace Eden.World.Sovereignty;

internal sealed partial class PilotEntosisStore
{
    internal void DropAllianceClaims(WorldAction world, int allianceId, long at)
    {
        var claims = world
            .Filter<SavedSovereigntyClaim>(claim => claim.AllianceId == allianceId)
            .OrderBy(claim => claim.SystemId)
            .ToArray();
        if (claims.Length > 0)
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            foreach (var claim in claims)
            {
                RemoveCampaignNodes(world, claim.SystemId);
                DestroyClaimedHub(world, catalog, claim.SystemId, claim.HubId, at);
            }
            world.AfterCommit(() => Interlocked.Increment(ref _viewRevision));
        }
        SovereigntyDevelopmentPersistence.ReconcileCapital(world, allianceId, at);
    }

    private void RemoveCampaignNodes(WorldAction world, int system)
    {
        foreach (var node in world.Filter<SavedSovereigntyNode>(row => row.SourceSystemId == system).ToArray())
        {
            StopTarget(world, node.Id);
            world.Remove(node);
        }
    }

    private void DestroyClaimedHub(WorldAction world, CatalogDbContext catalog, int system, long hubId, long at)
    {
        StopTarget(world, hubId);
        if (world.Set<SavedStructure>().Find(hubId) is { State: not StructureState.Destroyed } structure)
        {
            structure.State = StructureState.Destroyed;
            structure.StateStartedAt = at;
            structure.StateEndsAt = null;
        }
        if (world.Set<SavedSovereigntyClaim>().Find(system) is { } claim && claim.HubId == hubId)
        {
            world.Remove(claim);
        }
        if (world.Set<SavedSovereigntyHub>().Find(system) is { } hub && hub.HubId == hubId)
        {
            world.Remove(hub);
        }
        if (world.Set<SavedSovereigntyConflict>().Find(system) is { } conflict && conflict.HubId == hubId)
        {
            world.Remove(conflict);
        }
        SovereigntyResourcePersistence.DestroyHub(world, catalog, hubId, at);
        CapitalNavigationStore.StopUnavailableServices(world, system, at);
        StructureIndustry.StopUnavailableShipyards(world, catalog, system, at);
    }

    private void ReconcileClosedAllianceClaims()
    {
        // Older checkpoints could retain sovereignty after the owning alliance had closed.
        var closedOwners = Live.Economy.Inspect(world =>
            world
                .Set<SavedSovereigntyClaim>()
                .Select(claim => claim.AllianceId)
                .Distinct()
                .AsEnumerable()
                .Where(id => world.Set<SavedAlliance>().Find(id)?.ClosedAt is not null)
                .ToArray()
        );
        if (closedOwners.Length == 0)
        {
            return;
        }
        using var world = Live.Economy.Begin(TransactionParticipants.None);
        foreach (int allianceId in closedOwners)
        {
            if (world.Set<SavedAlliance>().Find(allianceId)?.ClosedAt is long closedAt)
            {
                DropAllianceClaims(world, allianceId, closedAt);
            }
        }
        world.Commit();
    }
}
