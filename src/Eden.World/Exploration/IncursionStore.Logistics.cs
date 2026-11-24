using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal sealed partial class IncursionStore
{
    internal static void RecordIncomingDamage(
        WorldAction world,
        CatalogDbContext catalog,
        WorldNpc npc,
        Pilot target,
        ShipHealth before,
        HullDamage after,
        long now
    )
    {
        if (
            world is not EconomyTransaction
            || npc.AnchorKind != NpcAnchorKind.Incursion
            || before.Remaining == after
            || world.Set<WorldCombatAnomaly>().Find(npc.EncounterId) is not { CompletedAt: null, IncursionTaleId: long taleId } site
            || now >= IncursionRules.EndsAt(taleId)
            || WorldPilotOnSite(world, catalog, site, target.Id, now) is null
        )
        {
            return;
        }

        var budget =
            world.Set<SavedIncursionRepairDamage>().Find(site.Id, target.ActiveShipId)
            ?? world
                .Add(
                    new SavedIncursionRepairDamage
                    {
                        SiteId = site.Id,
                        ShipId = target.ActiveShipId,
                        TaleId = taleId,
                        SystemId = site.SystemId,
                    }
                )
                .Entity;
        budget.Shield = Outstanding(budget.Shield, before.Defense.Shield, before.Remaining.Shield, after.Shield);
        budget.Armor = Outstanding(budget.Armor, before.Defense.Armor, before.Remaining.Armor, after.Armor);
        budget.Hull = Outstanding(budget.Hull, before.Defense.Structure, before.Remaining.Structure, after.Structure);
    }

    internal static void RecordRepair(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot source,
        Pilot recipient,
        RemoteRepairKind layer,
        double appliedHp,
        long now
    )
    {
        if (world is not EconomyTransaction || !double.IsFinite(appliedHp) || appliedHp <= 0)
        {
            return;
        }

        var budgets = world.Filter<SavedIncursionRepairDamage>(row => row.ShipId == recipient.ActiveShipId).ToArray();
        if (budgets.Length == 0)
        {
            return;
        }

        int systemId = CrimewatchPersistence.System(catalog, recipient.ActiveShip);
        foreach (var budget in budgets.OrderByDescending(row => row.SystemId == systemId).ThenByDescending(row => row.SiteId))
        {
            double spent = SpendRepairBudget(budget, layer, appliedHp);
            if (spent <= 0)
            {
                continue;
            }

            // Even an ineligible repair spends the same HP. Two pilots cannot claim that deficit.
            appliedHp -= spent;
            if (
                budget.SystemId == systemId
                && now < IncursionRules.EndsAt(budget.TaleId)
                && world.Set<WorldCombatAnomaly>().Find(budget.SiteId) is { CompletedAt: null } site
                && WorldPilotOnSite(world, catalog, site, source.Id, now) is not null
                && WorldPilotOnSite(world, catalog, site, recipient.Id, now) is not null
                && source.Id != recipient.Id
                && LiveWorld.Find(world.WorldPath)?.Fleets?.MembersInFleet(source.Id, [recipient.Id]).Contains(recipient.Id) == true
            )
            {
                Contribution(world, site, source.Id).Repair += spent;
            }
            if (appliedHp <= 0)
            {
                break;
            }
        }
    }

    private static double Outstanding(double previous, double maximum, double before, double after) =>
        Math.Min(previous, Math.Max(0, maximum - before)) + Math.Max(0, before - after);

    private static double SpendRepairBudget(SavedIncursionRepairDamage budget, RemoteRepairKind layer, double amount)
    {
        switch (layer)
        {
            case RemoteRepairKind.Shield:
                double shield = Math.Min(budget.Shield, amount);
                budget.Shield -= shield;
                return shield;
            case RemoteRepairKind.Armor:
                double armor = Math.Min(budget.Armor, amount);
                budget.Armor -= armor;
                return armor;
            default:
                double hull = Math.Min(budget.Hull, amount);
                budget.Hull -= hull;
                return hull;
        }
    }
}
