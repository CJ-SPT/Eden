using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Protocol.Tests.Combat;

public sealed class CombatDamageTests
{
    private static readonly DamageVector s_full = new(1, 1, 1, 1);

    [Fact]
    public void LayerOverflowPreservesDamageCompositionAndAppliesEachLayersOwnResistances()
    {
        HullDefense defense = new(10, 20, 30, 0, new(1, 1, 0.5, 0.5), new(1, 1, 0.5, 1), s_full);
        var result = CombatDamage.Apply(defense.Undamaged, defense, new(0, 0, 40, 40));
        // Shield consumes one quarter of the original 80 damage. The remaining
        // 60 meets armor's 0.75 weighted resonance, leaving 100/3 raw for hull.
        Assert.Equal(new HullDamage(0, 0, 0), result.Remaining);
        Assert.Equal(60, result.Applied, 10);
        result = CombatDamage.Apply(new(5, 20, 30), defense, new(0, 0, 20, 20));
        Assert.Equal(0, result.Remaining.Shield);
        Assert.Equal(0, result.Remaining.Armor);
        Assert.Equal(80d / 3, result.Remaining.Structure, 10);
        Assert.Equal(85d / 3, result.Applied, 10);
    }

    [Fact]
    public void FullyResistedDamageDoesNotLeakThroughAnIntactLayerAndMissesDoNoDamage()
    {
        HullDefense defense = new(10, 20, 30, 0, DamageVector.None, s_full, s_full);
        Assert.Equal(defense.Undamaged, CombatDamage.Apply(defense.Undamaged, defense, new(100, 100, 100, 100)).Remaining);
        Assert.Equal(defense.Undamaged, CombatDamage.Apply(defense.Undamaged, defense, DamageVector.None).Remaining);
        Assert.Equal(new HullDamage(0, 15, 30), CombatDamage.Apply(new(0, 20, 30), defense, new(0, 0, 5, 0)).Remaining);
        var damaged = new HullDamage(5, 10, 15);
        Assert.Equal(new HullDamage(0.5, 0.5, 0.5), damaged.Fractions(defense));
        Assert.Throws<ArgumentOutOfRangeException>(() => CombatDamage.Apply(damaged, defense, new(-1, 0, 0, 0)));
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(4, 5)]
    [InlineData(5, 0)]
    public void ShieldPenetrationUsesFittedThresholdAndConservesDamage(int skill, int threshold)
    {
        var attributes = FittingRules.Apply(
            new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.ShieldUniformity] = .75 },
            new Dictionary<int, int> { [(int)DefenseSkill.TacticalShieldManipulation] = skill },
            []
        );
        HullDefense defense = new(100, 100, 100, 0, s_full, new(.5, 1, 1, 1), s_full)
        {
            ShieldUniformity = ShipHealth.DefenseFrom(attributes).ShieldUniformity,
        };
        Assert.Equal(threshold, 100 * (1 - defense.ShieldUniformity), 8);
        bool penetrated = false;
        for (long time = 0; time < 512; time++)
        {
            HullDamage low = new(2, 100, 100);
            DamageVector hit = new(1, 0, 0, 0);
            var result = CombatDamage.Apply(low, defense, hit, 42, time);
            double leaked = (100 - result.Remaining.Armor) * 2;
            Assert.Equal(1, 2 - result.Remaining.Shield + leaked, 10);
            Assert.Equal(result, CombatDamage.Apply(low, defense, hit, 42, time));
            penetrated |= leaked > 0;
            var above = CombatDamage.Apply(low with { Shield = Math.Max(1, threshold) }, defense, hit, 42, time);
            Assert.Equal(100, above.Remaining.Armor);
        }
        Assert.Equal(skill < 5, penetrated);
    }

    [Fact]
    public void RangeAndTrackingPenaltiesCombine()
    {
        Assert.Equal(1, CombatDamage.HitChance(4800, 0, 40, 4800, 2000, 500, 40000));
        Assert.Equal(0.5, CombatDamage.HitChance(6800, 0, 40, 4800, 2000, 500, 40000));
        Assert.Equal(0.0625, CombatDamage.HitChance(8800, 0, 40, 4800, 2000, 500, 40000));
        Assert.Equal(0.5, CombatDamage.HitChance(4800, 0.5, 40, 4800, 2000, 500, 40000));
        Assert.Equal(0.25, CombatDamage.HitChance(6800, 0.5, 40, 4800, 2000, 500, 40000));
        Assert.Equal(0, CombatDamage.HitChance(4801, 0, 40, 4800, 0, 0, 40000));
        Assert.Throws<ArgumentOutOfRangeException>(() => CombatDamage.HitChance(double.NaN, 0, 40, 4800, 2000, 500, 40000));
    }

    [Fact]
    public void HitQualityUsesTheSameRollForHitChanceAndWreckingShots()
    {
        Assert.Equal(0, CombatDamage.ShotQuality(0, 0));
        Assert.Equal(0, CombatDamage.ShotQuality(0.5, 0.5));
        Assert.Equal(3, CombatDamage.ShotQuality(0.5, 0.005));
        Assert.Equal(0, CombatDamage.ShotQuality(0.001, 0.005));
        Assert.Equal(0.99, CombatDamage.ShotQuality(1, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => CombatDamage.ShotQuality(1, 1));
    }
}
