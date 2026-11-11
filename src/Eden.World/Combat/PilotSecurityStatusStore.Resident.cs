using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Combat;

internal sealed partial class PilotSecurityStatusStore
{
    internal static void RecordKill(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        WorldNpc npc,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        long now
    )
    {
        if (!NpcPopulationRules.RewardsPirateKill(npc.Role))
        {
            return;
        }

        double bonus = attributes.GetValueOrDefault(DogmaAttributeId.EntitySecurityStatusKillBonus);
        if (bonus == 0)
        {
            return;
        }
        Settle(world, pilot.Id, now);
        double current = world.Set<SavedSecurityStatus>().Find(pilot.Id)?.Value ?? 0;
        int level = PilotSkillReader.Level(world, catalog, pilot.Id, (int)SecuritySkill.FastTalk);
        if (level > 0)
        {
            var account = world.Set<Account>().Find(pilot.AccountId)!;
            if (account.CloneGrade == AccountCloneGrade.Alpha)
            {
                var alpha = catalog.Require(CatalogData.Client.Clonegrades, pilot.RaceId.ToString(CultureInfo.InvariantCulture));
                int cap = checked((int)(alpha.Skills?.SingleOrDefault(s => s.TypeID == (int)SecuritySkill.FastTalk)?.Level ?? 0));
                level = Math.Min(level, cap);
            }
        }
        double mutator =
            level == 0 ? 0 : catalog.FittingAttributes((int)SecuritySkill.FastTalk).GetValueOrDefault(DogmaAttributeId.FastTalkMutator, 0);
        var reward = SecurityStatusRules.Reward(
            bonus,
            attributes.GetValueOrDefault(DogmaAttributeId.EntitySecurityMaxGain),
            level,
            mutator
        );
        double gain = SecurityStatusRules.Apply(current, reward).After - current;
        if (gain <= 0)
        {
            return;
        }
        var pending = world.Set<PendingSecurityReward>().Find(pilot.Id);
        if (pending is not null && SecurityStatusRules.Apply(current, new(pending.Fraction, pending.Maximum)).After - current >= gain)
        {
            return;
        }
        if (pending is null)
        {
            pending = world
                .Add(new PendingSecurityReward { PilotId = pilot.Id, Due = checked(now + SecurityStatusRules.RewardWindow.Ticks) })
                .Entity;
        }
        pending.NpcId = npc.Id;
        pending.TypeId = npc.TypeId;
        pending.SystemId = npc.SystemId;
        pending.CorporationId = npc.CorporationId;
        pending.Fraction = reward.Fraction;
        pending.Maximum = reward.Maximum;
    }

    private static bool Settle(WorldAction world, long pilotId, long now)
    {
        var pending = world.Set<PendingSecurityReward>().Find(pilotId);
        if (pending is null || pending.Due > now)
        {
            return false;
        }
        var state = world.Set<SavedSecurityStatus>().Find(pilotId) ?? world.Add(new SavedSecurityStatus { PilotId = pilotId }).Entity;
        var change = SecurityStatusRules.Apply(state.Value, new(pending.Fraction, pending.Maximum));
        state.Value = change.After;
        if (change.Modification > 0)
        {
            world.Add(
                new SavedSecurityTransaction
                {
                    PilotId = pilotId,
                    Time = pending.Due,
                    NpcId = pending.NpcId,
                    TypeId = pending.TypeId,
                    SystemId = pending.SystemId,
                    CorporationId = pending.CorporationId,
                    Modification = change.Modification,
                    NewValue = change.After,
                }
            );
        }
        world.Remove(pending);
        // A kill at the exact boundary must be able to open a fresh pending row
        // with the same pilot key in this transaction before it commits.
        world.SaveChanges();
        return true;
    }
}
