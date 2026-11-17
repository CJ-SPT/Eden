using Eden.Gameplay.Sovereignty;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyDevelopmentRulesTests
{
    [Fact]
    public void CreditsSplitAtClientBandsAndDecayPreservesSubsecondBoundaries()
    {
        double width = SovereigntyDevelopmentRules.LevelWidth;
        var state = new SovereigntyActivity(width - 5, 0, TimeSpan.TicksPerDay);
        var result = SovereigntyDevelopmentRules.Credit(state, SovereigntyActivityKind.Military, 1, state.UpdatedAt);
        Assert.Equal(width + 29997, result.Military, 5);
        Assert.Equal(1, SovereigntyDevelopmentRules.Level(result.Military));
        var full = new SovereigntyActivity(width * 2, width, state.UpdatedAt);
        var decayed = SovereigntyDevelopmentRules.Decay(full, state.UpdatedAt + TimeSpan.FromHours(96).Ticks);
        Assert.Equal(width, decayed.Military);
        Assert.Equal(0, decayed.Industrial);
        Assert.Equal(full, SovereigntyDevelopmentRules.Decay(full, state.UpdatedAt - 1));
        var crossed = SovereigntyDevelopmentRules.Decay(full, state.UpdatedAt + 1);
        Assert.Equal(1, SovereigntyDevelopmentRules.Level(crossed.Military));
        Assert.Equal(state.UpdatedAt + 1, SovereigntyDevelopmentRules.NextLevelChange(full, state.UpdatedAt));
    }

    [Theory]
    [InlineData(SovereigntyActivityKind.Military)]
    [InlineData(SovereigntyActivityKind.Industrial)]
    public void HugeCreditsSaturateWithoutOverflowAndInvalidInputsLeaveStateUnchanged(SovereigntyActivityKind kind)
    {
        var state = new SovereigntyActivity(0, 0, 1);
        var result = SovereigntyDevelopmentRules.Credit(state, kind, 1e300, 1);
        Assert.Equal(
            SovereigntyDevelopmentRules.MaximumPoints,
            kind == SovereigntyActivityKind.Military ? result.Military : result.Industrial
        );
        Assert.Throws<InvalidDataException>(() => SovereigntyDevelopmentRules.Credit(state, kind, double.NaN, 1));
        Assert.Throws<InvalidDataException>(() => SovereigntyDevelopmentRules.Credit(state, kind, -1, 1));
        Assert.Equal(0, state.Military);
        Assert.Equal(0, state.Industrial);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void AdmSlowsOnlyAttackersAndNeverDefendersOrPassiveRegeneration(int multiplier)
    {
        var attacker = SovereigntyWarfareRules.Advance(new(1, 0), [new(2, 0, 1000)], 50, 100, .2, 1, false, attackMultiplier: multiplier);
        Assert.Equal(1 - .5 / multiplier, attacker.Value, 10);
        var defender = SovereigntyWarfareRules.Advance(new(0, 0), [new(1, 0, 1000)], 50, 100, .2, 1, false, attackMultiplier: multiplier);
        Assert.Equal(.5, defender.Value, 10);
        var passive = SovereigntyWarfareRules.Advance(new(0, 0), [], 50, 100, .2, 1, false, attackMultiplier: multiplier);
        Assert.Equal(.1, passive.Value, 10);
    }
}
