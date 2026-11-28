using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Sovereignty;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Sovereignty;

internal sealed record SovereigntyEntosisTarget(int SystemId, long Id, bool CommandNode);

internal sealed record SovereigntyDefenseObjective(
    int ClaimSystemId,
    SovereigntyEntosisTarget Target,
    bool HostileLink,
    double ClaimProgress,
    double TargetProgress
);

internal sealed partial class PilotEntosisStore
{
    internal IEnumerable<SovereigntyDefenseObjective> DefenseTargets(
        WorldAction world,
        IReadOnlyList<SavedSovereigntyClaim> holdings,
        long now
    ) =>
        holdings
            .SelectMany(claim => DefenseObjectives(world, claim, now))
            .OrderByDescending(objective => objective.HostileLink)
            .ThenBy(objective => objective.ClaimProgress)
            .ThenBy(objective => objective.TargetProgress)
            .ThenBy(objective => objective.Target.Id);

    private IEnumerable<SovereigntyDefenseObjective> DefenseObjectives(WorldAction world, SavedSovereigntyClaim claim, long now)
    {
        if (
            world.Set<SavedSovereigntyConflict>().Find(claim.SystemId) is not { } conflict
            || conflict.HubId != claim.HubId
            || conflict.DefenderAllianceId != claim.AllianceId
        )
        {
            yield break;
        }
        if (conflict.Phase == SovereigntyPhase.Campaign)
        {
            foreach (var node in CampaignNodes(world, claim))
            {
                yield return new(
                    claim.SystemId,
                    new(node.SystemId, node.Id, true),
                    HasHostileLink(world, node.Id, claim.AllianceId, now),
                    conflict.DefenderPoints / (double)SovereigntyWarfareRules.MaximumDefenderPoints,
                    node.Value
                );
            }
        }
        else if (Target(world, claim.SystemId, claim.HubId) is { } target && Admits(world, target, now))
        {
            bool hostile = HasHostileLink(world, claim.HubId, claim.AllianceId, now);
            if (hostile || conflict.Value < 1)
            {
                yield return new(claim.SystemId, new(claim.SystemId, claim.HubId, false), hostile, conflict.Value, conflict.Value);
            }
        }
    }

    private static IEnumerable<SovereigntyEntosisTarget> CampaignTargets(WorldAction world, SavedSovereigntyClaim claim) =>
        CampaignNodes(world, claim)
            .OrderBy(node => node.Value)
            .ThenBy(node => node.Id)
            .Select(node => new SovereigntyEntosisTarget(node.SystemId, node.Id, true));

    private static IEnumerable<SavedSovereigntyNode> CampaignNodes(WorldAction world, SavedSovereigntyClaim claim) =>
        world
            .Filter<SavedSovereigntyNode>(node => node.SourceSystemId == claim.SystemId)
            .AsEnumerable()
            .Where(node => node.HubId == claim.HubId && node.AllianceId == claim.AllianceId);

    internal bool DefenseNeeded(WorldAction world, int claimSystem, int alliance, int targetSystem, long id, long now)
    {
        if (
            world.Set<SavedSovereigntyClaim>().Find(claimSystem) is not { } claim
            || claim.AllianceId != alliance
            || world.Set<SavedSovereigntyConflict>().Find(claimSystem) is not { } conflict
            || conflict.HubId != claim.HubId
            || conflict.DefenderAllianceId != alliance
            || Target(world, targetSystem, id) is not { } target
            || !Admits(world, target, now)
        )
        {
            return false;
        }
        if (world.Set<SavedSovereigntyNode>().Find(id) is { } node)
        {
            return node.SourceSystemId == claimSystem && node.HubId == claim.HubId && node.AllianceId == alliance;
        }
        return id == claim.HubId && (conflict.Value < 1 || HasHostileLink(world, id, alliance, now));
    }

    private bool HasHostileLink(WorldAction world, long target, int defender, long now) =>
        world
            .Filter<SavedEntosisCycle>(cycle => cycle.HubId == target)
            .AsEnumerable()
            .Any(cycle => cycle.AllianceId != defender && cycle.EndsAt > now && cycle.Contributing && CanScore(cycle.ModuleId));

    internal static bool ContestedHub(WorldAction world, SavedSovereigntyClaim claim, long now) =>
        world.Set<SavedSovereigntyConflict>().Find(claim.SystemId) is { } conflict
        && conflict.HubId == claim.HubId
        && conflict.DefenderAllianceId == claim.AllianceId
        && (
            conflict.Phase == SovereigntyPhase.Campaign
            || conflict.Value < 1 && Target(world, claim.SystemId, claim.HubId) is { } target && Admits(world, target, now)
        );

    internal static StaticSpaceObject? ExpeditionTarget(WorldAction world, CatalogDbContext catalog, int system, long id)
    {
        if (Target(world, system, id) is not { } target || catalog.FittingType(target.TypeId) is not { } type)
        {
            return null;
        }
        var node = world.Set<SavedSovereigntyNode>().Find(id);
        int owner = node?.CorporationId ?? world.Set<Eden.Data.World.Structures.SavedStructure>().Find(id)!.CorporationId;
        return new(id, target.TypeId, type.GroupId, type.CategoryId, owner, target.Position, (float)(type.Radius ?? 0))
        {
            Solid = node is null,
        };
    }
}
