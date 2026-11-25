using Eden.Data.World.Corporations;
using Eden.Data.World.Structures;
using Eden.Gameplay.Corporations;
using Eden.World.Runtime.Economy;

namespace Eden.World.Corporations;

internal sealed partial class CorporationWarStore
{
    internal static void InheritDepartingWars(WorldAction world, int allianceId, int corporationId, long now)
    {
        if (world is not EconomyTransaction || world.Set<SavedCorporation>().Find(corporationId) is not { ClosedAt: null })
        {
            return;
        }
        bool eligible = EligibilityLoss(world, corporationId, now) is null;
        foreach (var source in ForOrganization(world, allianceId).Where(war => Finish(war) is null || Finish(war) > now).ToArray())
        {
            bool aggressorLeaves = source.AttackerId == allianceId;
            int attacker = aggressorLeaves ? corporationId : source.AttackerId;
            int defender = aggressorLeaves ? source.DefenderId : corporationId;
            if (
                attacker == defender
                || ForPair(world, attacker, defender).Any(war => war.InheritedFromWarId == source.Id && war.InheritedAt == now)
            )
            {
                continue;
            }

            long? headquarters = source.HeadquartersId;
            if (aggressorLeaves && (headquarters is not long id || world.Set<SavedStructure>().Find(id)?.CorporationId != corporationId))
            {
                headquarters = null;
            }
            var inherited = new SavedCorporationWar
            {
                Id = world.Next(CorporationWarSchema.WarTable),
                AttackerId = attacker,
                DefenderId = defender,
                HeadquartersId = headquarters,
                DeclaredAt = source.DeclaredAt,
                StartsAt = source.StartsAt,
                EndingAt = source.EndingAt,
                EndsAt = source.EndsAt,
                RetractedAt = source.RetractedAt,
                RetractedBy = source.RetractedBy,
                Mutual = source.Mutual,
                OpenForAllies = source.OpenForAllies,
                ReasonEnded = source.ReasonEnded,
                InheritedFromWarId = source.Id,
                InheritedAt = now,
            };
            if (!inherited.Mutual && inherited.EndsAt is null)
            {
                ApplyInheritedBilling(world, inherited, source, eligible, aggressorLeaves, now);
            }
            world.Add(inherited);
            foreach (var ally in AlliesForWar(world, source.Id, now).ToArray())
            {
                CopyAlly(world, ally, inherited, ally.OwnerId, now);
            }
        }
        foreach (var ally in world.Filter<SavedCorporationWarAlly>(row => row.OwnerId == allianceId && row.EndsAt > now).ToArray())
        {
            if (world.Set<SavedCorporationWar>().Find(ally.WarId) is { } war && Finish(war) > now)
            {
                CopyAlly(world, ally, war, corporationId, now);
            }
        }
    }

    private static void CopyAlly(WorldAction world, SavedCorporationWarAlly source, SavedCorporationWar war, int owner, long now)
    {
        if (owner == war.AttackerId || owner == war.DefenderId || world.Set<SavedCorporationWarAlly>().Find(war.Id, owner) is not null)
        {
            return;
        }
        long endsAt = Math.Min(source.EndsAt, war.EndsAt ?? long.MaxValue);
        if (endsAt > Math.Max(now, source.StartsAt))
        {
            world.Add(
                new SavedCorporationWarAlly
                {
                    WarId = war.Id,
                    OwnerId = owner,
                    AttackerId = war.AttackerId,
                    StartsAt = source.StartsAt,
                    EndsAt = endsAt,
                }
            );
        }
    }

    private static void ApplyInheritedBilling(
        WorldAction world,
        SavedCorporationWar inherited,
        SavedCorporationWar source,
        bool eligible,
        bool aggressorLeaves,
        long now
    )
    {
        long due = source.BillDueAt ?? now;
        if (due <= now)
        {
            inherited.EndingAt = due;
            inherited.EndsAt = checked(due + CorporationWarRules.Cooldown);
            inherited.ReasonEnded = CorporationWarEndReason.BillUnpaid;
        }
        else if (!eligible)
        {
            inherited.EndingAt = now;
            inherited.EndsAt = checked(Math.Max(inherited.StartsAt, now) + CorporationWarRules.Cooldown);
            inherited.ReasonEnded = aggressorLeaves
                ? CorporationWarEndReason.AttackerIneligible
                : CorporationWarEndReason.DefenderIneligible;
        }
        else if (inherited.HeadquartersId is null)
        {
            // Preserve the paid cycle. A corporation cannot renew an offensive war using a different organization's headquarters.
            inherited.EndingAt = due;
            inherited.EndsAt = checked(due + CorporationWarRules.Cooldown);
            inherited.ReasonEnded = CorporationWarEndReason.AllianceDeparted;
        }
        else
        {
            inherited.BillDueAt = due;
            inherited.BillIssuedAt = now;
            inherited.BillId = world.Next("WorldAllianceBill");
        }
    }
}
