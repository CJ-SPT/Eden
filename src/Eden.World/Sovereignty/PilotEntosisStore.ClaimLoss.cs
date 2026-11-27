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

    internal void ReconcileAllianceClaimCustody(WorldAction world, int allianceId)
    {
        if (
            world.Set<SavedAlliance>().Find(allianceId) is not { ClosedAt: null, ExecutorCorporationId: int executor }
            || !IsAllianceMember(world, allianceId, executor)
        )
        {
            return;
        }
        var claims = world
            .Filter<SavedSovereigntyClaim>(claim => claim.AllianceId == allianceId)
            .AsEnumerable()
            .Where(claim => !IsAllianceMember(world, allianceId, claim.CorporationId))
            .ToArray();
        foreach (var claim in claims)
        {
            claim.CorporationId = executor;
            if (world.Set<SavedStructure>().Find(claim.HubId) is { State: not StructureState.Destroyed } hub)
            {
                hub.CorporationId = executor;
            }
            foreach (var node in world.Filter<SavedSovereigntyNode>(row => row.HubId == claim.HubId))
            {
                node.CorporationId = executor;
            }
        }
        if (claims.Length > 0)
        {
            world.AfterCommit(() => Interlocked.Increment(ref _viewRevision));
        }
    }

    private static bool IsAllianceMember(WorldAction world, int allianceId, int corporationId) =>
        world.Set<SavedAllianceMember>().Find(corporationId)?.AllianceId == allianceId;

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

    private void ReconcileSavedAllianceClaims()
    {
        // Older checkpoints could retain sovereignty after closure or keep a departed corporation as custodian.
        var owners = Live.Economy.Inspect(world =>
            world
                .Set<SavedSovereigntyClaim>()
                .AsEnumerable()
                .Where(claim =>
                    world.Set<SavedAlliance>().Find(claim.AllianceId) is { } alliance
                    && (
                        alliance.ClosedAt.HasValue
                        || alliance.ExecutorCorporationId.HasValue && !IsAllianceMember(world, claim.AllianceId, claim.CorporationId)
                    )
                )
                .Select(claim => claim.AllianceId)
                .Distinct()
                .ToArray()
        );
        if (owners.Length == 0)
        {
            return;
        }
        using var world = Live.Economy.Begin(TransactionParticipants.None);
        foreach (int allianceId in owners)
        {
            if (world.Set<SavedAlliance>().Find(allianceId)?.ClosedAt is long closedAt)
            {
                DropAllianceClaims(world, allianceId, closedAt);
            }
            else
            {
                ReconcileAllianceClaimCustody(world, allianceId);
            }
        }
        world.Commit();
    }
}
