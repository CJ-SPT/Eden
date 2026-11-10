using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class RattingRulesTests
{
    private static readonly DamageVector Resonance = new(1, 1, 1, 1);

    private static HullDefense Defense(double hp) => new(hp, hp, hp, 0, Resonance, Resonance, Resonance);

    private static ShipHealth Pilot => new(1, Defense(1000), Defense(1000).Undamaged);
    private static RattingWeapon Weapon => new(new(1, 1, 1, new(100, 0, 0, 0), 1000, 20000, 1000, 1, 40, 1), 1000);
    private static RattingOpponent Enemy => new(2, Defense(100), Defense(100).Undamaged, 40, 0, 5000, new(1, 0, 0, 0), 0, 0);

    [Fact]
    public void ActualFitMustClearTheWholeGroupAndSurviveTwiceAsLong()
    {
        var capable = RattingRules.Assess(Pilot, 1000, [Weapon], [Enemy]);
        Assert.True(capable.Capable);
        Assert.True(capable.ClearSeconds < 10);
        Assert.True(capable.SurvivalSeconds >= capable.ClearSeconds * 2);
        Assert.False(
            RattingRules.Assess(Pilot, 1000, [Weapon], Enumerable.Range(0, 100).Select(i => Enemy with { Id = i }).ToArray()).Capable
        );
        Assert.False(RattingRules.Assess(Pilot, 1000, [Weapon], [Enemy with { DamagePerSecond = new(10000, 0, 0, 0) }]).Capable);
    }

    [Fact]
    public void RepairsApplicationCapacitorAndAmmunitionCanEachRejectACombatHull()
    {
        Assert.False(RattingRules.Assess(Pilot, 1000, [], [Enemy]).Capable);
        Assert.False(RattingRules.Assess(Pilot, 0, [Weapon], [Enemy]).Capable);
        Assert.False(RattingRules.Assess(Pilot, 1000, [Weapon with { AvailableShots = 1 }], [Enemy]).Capable);
        Assert.False(RattingRules.Assess(Pilot, 1000, [Weapon], [Enemy with { ShieldRepairPerSecond = 1000 }]).Capable);
        Assert.False(RattingRules.Assess(Pilot, 1000, [Weapon], [Enemy with { Signature = 1, Speed = 10000 }]).Capable);
        Assert.False(RattingRules.Assess(Pilot with { Remaining = new(0, 0, 1) }, 1000, [Weapon], [Enemy]).Capable);
    }

    [Fact]
    public void ChoiceAndOptionalProgressSurviveSerializationWithoutRerolling()
    {
        var choices = Enumerable.Range(1, 300).Select(i => RattingRules.Choose(123, i)).ToArray();
        Assert.InRange(choices.Count(c => c), 70, 130);
        Assert.Equal(choices, Enumerable.Range(1, 300).Select(i => RattingRules.Choose(123, i)));
        var progress = new RattingProgress(60015157, [1, 2, 3], 1, 120)
        {
            Phase = RattingPhase.Fighting,
            TargetId = 123,
            LootIds = [456],
        };
        var restored = System.Text.Json.JsonSerializer.Deserialize<RattingProgress>(System.Text.Json.JsonSerializer.Serialize(progress))!;
        Assert.Equal(progress.TargetId, restored.TargetId);
        Assert.Equal(progress.Belts, restored.Belts);
        Assert.Equal(progress.LootIds, restored.LootIds);
        Assert.Equal(progress.RemainingTicks, restored.RemainingTicks);
    }
}
