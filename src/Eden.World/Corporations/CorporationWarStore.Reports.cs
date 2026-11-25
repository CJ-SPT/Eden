using Eden.Data.World.Corporations;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Corporations;

internal sealed partial class CorporationWarStore
{
    internal sealed record LossWar(long Id, int VictimOrganizationId, bool AttackerLoss);

    internal static LossWar? WarForLoss(WorldAction world, CatalogDbContext catalog, ShipCombatReport report)
    {
        if (world is not EconomyTransaction resident || !resident.MayHaveCorporationWars())
        {
            return null;
        }
        long now = report.Time;
        int victim = Organization(world, report.Victim.CorporationId);
        var candidates = ForOrganization(world, victim).Where(war => Active(war, now)).ToList();
        foreach (var ally in world.Filter<SavedCorporationWarAlly>(row => row.OwnerId == victim))
        {
            if (ActiveAlly(world, ally, now) && world.Set<SavedCorporationWar>().Find(ally.WarId) is { } allyWar)
            {
                candidates.Add(allyWar);
            }
        }
        if (candidates.Count == 0)
        {
            return null;
        }
        var attackers = report
            .OtherAttackers.Append(report.FinalBlow)
            .Where(party => party.Damage > 0)
            .Select(party => EffectiveCombatOrganization(world, catalog, party))
            .ToHashSet();
        foreach (var war in candidates.OrderByDescending(war => war.Id))
        {
            if (victim == war.AttackerId)
            {
                if (
                    attackers.Contains(war.DefenderId)
                    || world
                        .Filter<SavedCorporationWarAlly>(row => row.WarId == war.Id)
                        .Any(ally => attackers.Contains(ally.OwnerId) && ActiveAlly(world, ally, now))
                )
                {
                    return new(war.Id, victim, true);
                }
            }
            else if (attackers.Contains(war.AttackerId))
            {
                return new(war.Id, victim, false);
            }
        }
        return null;
    }

    private static int EffectiveCombatOrganization(WorldAction world, CatalogDbContext catalog, CombatParty party)
    {
        bool structure = party.ShipTypeId is int type && catalog.FittingType(type)?.CategoryId == (int)InventoryCategory.Structure;
        int corporation =
            !structure && party.CharacterId is long pilotId && world.Pilots.Find(pilotId) is { } pilot
                ? pilot.CorporationId
                : party.CorporationId;
        return Organization(world, corporation);
    }
}
