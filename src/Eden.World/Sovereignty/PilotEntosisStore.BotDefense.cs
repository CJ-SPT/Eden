using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Sovereignty;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Sovereignty;

internal sealed record SovereigntyEntosisTarget(int SystemId, long Id, bool CommandNode);

internal sealed partial class PilotEntosisStore
{
    internal IEnumerable<SovereigntyEntosisTarget> DefenseTargets(WorldAction world, SavedSovereigntyClaim home, long now)
    {
        var conflict = world.Set<SavedSovereigntyConflict>().Find(home.SystemId);
        if (conflict?.Phase == SovereigntyPhase.Campaign)
        {
            return CampaignTargets(world, home);
        }
        return DefenseNeeded(world, home.SystemId, home.AllianceId, home.SystemId, home.HubId, now)
            ? [new(home.SystemId, home.HubId, false)]
            : [];
    }

    private static IEnumerable<SovereigntyEntosisTarget> CampaignTargets(WorldAction world, SavedSovereigntyClaim claim) =>
        world
            .Filter<SavedSovereigntyNode>(node => node.SourceSystemId == claim.SystemId)
            .AsEnumerable()
            .Where(node => node.HubId == claim.HubId && node.AllianceId == claim.AllianceId)
            .OrderBy(node => node.Value)
            .ThenBy(node => node.Id)
            .Select(node => new SovereigntyEntosisTarget(node.SystemId, node.Id, true));

    internal bool DefenseNeeded(WorldAction world, int homeSystem, int alliance, int system, long id, long now)
    {
        if (
            world.Set<SavedSovereigntyClaim>().Find(homeSystem) is not { } claim
            || claim.AllianceId != alliance
            || Target(world, system, id) is not { } target
            || !Admits(world, target, now)
        )
        {
            return false;
        }
        if (world.Set<SavedSovereigntyNode>().Find(id) is { } node)
        {
            return node.SourceSystemId == homeSystem && node.HubId == claim.HubId && node.AllianceId == alliance;
        }
        return id == claim.HubId
            && world.Set<SavedSovereigntyConflict>().Find(homeSystem) is { } conflict
            && (
                conflict.Value < 1
                || world
                    .Filter<SavedEntosisCycle>(cycle => cycle.HubId == id)
                    .AsEnumerable()
                    .Any(cycle => cycle.AllianceId != alliance && cycle.EndsAt > now && cycle.Contributing && CanScore(cycle.ModuleId))
            );
    }

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
