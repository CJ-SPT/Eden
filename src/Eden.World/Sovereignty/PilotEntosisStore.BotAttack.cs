using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Sovereignty;

internal sealed partial class PilotEntosisStore
{
    internal static IEnumerable<SovereigntyEntosisTarget> AttackTargets(WorldAction world, SavedSovereigntyClaim claim, long now)
    {
        if (
            world.Set<SavedSovereigntyConflict>().Find(claim.SystemId) is { Phase: SovereigntyPhase.Campaign } conflict
            && conflict.HubId == claim.HubId
            && conflict.DefenderAllianceId == claim.AllianceId
        )
        {
            return CampaignTargets(world, claim);
        }
        return AttackTargetAdmitted(world, claim, claim.SystemId, claim.HubId, now) ? [new(claim.SystemId, claim.HubId, false)] : [];
    }

    internal static bool AttackTargetAdmitted(WorldAction world, SavedSovereigntyClaim claim, int system, long id, long now)
    {
        if (
            world.Set<SavedSovereigntyConflict>().Find(claim.SystemId) is not { } conflict
            || conflict.HubId != claim.HubId
            || conflict.DefenderAllianceId != claim.AllianceId
            || Target(world, system, id) is not { } target
            || !Admits(world, target, now)
        )
        {
            return false;
        }
        return world.Set<SavedSovereigntyNode>().Find(id) is { } node
            ? node.SourceSystemId == claim.SystemId && node.HubId == claim.HubId && node.AllianceId == claim.AllianceId
            : id == claim.HubId && system == claim.SystemId;
    }

    internal static long AttackDuration(
        WorldAction world,
        CatalogDbContext catalog,
        SavedSovereigntyClaim claim,
        SovereigntyEntosisTarget target
    )
    {
        var conflict = world.Set<SavedSovereigntyConflict>().Find(claim.SystemId)!;
        var node = target.CommandNode ? world.Set<SavedSovereigntyNode>().Find(target.Id)! : null;
        double distance = node is null ? conflict.Value : node.Value + 1;
        double multiplier = node is null ? conflict.AppliedAdm : conflict.CampaignAdm;
        return (long)Math.Ceiling(EntosisCatalog.Defense(catalog, target.CommandNode).Duration * distance * multiplier);
    }

    internal static bool AttackCanWarmUp(WorldAction world, int source, long target, long now, long leadTime) =>
        world.Set<SavedSovereigntyConflict>().Find(source) is { } conflict
        && (target != conflict.HubId || conflict.Value < 1 || Window(world, conflict, now).EndsAt - now > leadTime);
}
