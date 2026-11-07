using Eden.Gameplay.Characters;

namespace Eden.Protocol.Tests.Combat;

public sealed class CombatContributionTests
{
    [Fact]
    public void SuccessiveHitsPreserveEarlierSnapshotsAndReplaceOnlyTheAttackersMetadata()
    {
        CombatParty first = new(1, 2, 3, null, 601, 3638, null, 10);
        IReadOnlyDictionary<long, CombatParty> original = new Dictionary<long, CombatParty> { [1] = first };
        var paid = InteractionHistoryRules.AddDamage(original, first with { WeaponTypeId = 99 }, 5);
        var following = InteractionHistoryRules.AddDamage(paid, new(7, null, 8, 9, 30380, 562, null, 0), 12);
        Assert.Equal(10, original[1].Damage);
        Assert.Equal(3638, original[1].WeaponTypeId);
        Assert.Equal(15, paid[1].Damage);
        Assert.Equal(99, paid[1].WeaponTypeId);
        Assert.Single(paid);
        Assert.Equal(2, following.Count);
        Assert.Equal(12, following[7].Damage);
        Assert.Equal(paid[1], following[1]);
        Assert.Same(following, InteractionHistoryRules.AddDamage(following, first, 0));
    }

    [Fact]
    public void RepeatedHitsOnManyAttackersKeepHistoryAllocationsBounded()
    {
        const int attackers = 2000;
        IReadOnlyDictionary<long, CombatParty> history = new Dictionary<long, CombatParty>();
        var seed = new CombatParty(1, null, 1, null, 30380, 562, null, 0);
        InteractionHistoryRules.AddDamage(history, seed, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int cycle = 0; cycle < 2; cycle++)
        {
            for (int index = 0; index < attackers; index++)
            {
                history = InteractionHistoryRules.AddDamage(history, seed with { EntityId = index }, 1);
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(attackers, history.Count);
        Assert.All(history.Values, party => Assert.Equal(2, party.Damage));
        Assert.InRange(allocated, 0, attackers * 8192L);
    }
}
