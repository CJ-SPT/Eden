using Eden.Gameplay.Sovereignty;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyWarfareRulesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(12)]
    [InlineData(23)]
    public void ReinforcementUsesTheWindowTwoDaysLaterAcrossUtcMidnight(int hour)
    {
        long at = TimeSpan.FromDays(100).Ticks + TimeSpan.FromHours(hour).Ticks;
        var window = SovereigntyWarfareRules.Window(18, at + TimeSpan.FromDays(2).Ticks);
        Assert.InRange(SovereigntyWarfareRules.CampaignDeadline(at, 18, 1234), window.StartsAt, window.EndsAt - 1);
        Assert.Equal(
            TimeSpan.FromHours(3).Ticks,
            SovereigntyWarfareRules.Window(18, at, 6m).EndsAt - SovereigntyWarfareRules.Window(18, at, 6m).StartsAt
        );
    }

    [Fact]
    public void OwnedHubAttackersShareOneSideAndDefendersPauseWithoutStacking()
    {
        var attack = SovereigntyWarfareRules.Advance(new(1, 0), [new(2, 0, 1000), new(3, 0, 1000)], 100, 1000, .2, 1, false);
        Assert.Equal(.9, attack.Value, 9);
        var blocked = SovereigntyWarfareRules.Advance(attack, [new(1, 100, 1000), new(2, 100, 1000)], 200, 1000, .2, 1, false);
        Assert.Equal(.9, blocked.Value, 9);
        Assert.True(blocked.Blocked);
        var reinforced = SovereigntyWarfareRules.Advance(blocked, [new(2, 200, 2000)], 1500, 1000, .2, 1, false);
        Assert.Equal(1100, reinforced.CompletedAt);
        Assert.Equal(0, reinforced.Value);
    }

    [Fact]
    public void PrimeTransitionKeepsOldWindowAndReinforcementDeadlineCannotReroll()
    {
        long at = TimeSpan.FromDays(100).Ticks;
        var saved = new AlliancePrimeTime(18, 6, at);
        Assert.Equal(18, SovereigntyWarfareRules.Prime(saved, at - 1).CurrentHour);
        var prime = SovereigntyWarfareRules.Prime(saved, at);
        Assert.Equal(6, prime.CurrentHour);
        Assert.Equal(18, prime.PreviousHour);
        Assert.Null(SovereigntyWarfareRules.Prime(saved, at + TimeSpan.TicksPerDay).PreviousHour);
        Assert.Equal(TimeSpan.FromHours(48).Ticks, SovereigntyWarfareRules.PrimeChangeDelay);
        var window = SovereigntyWarfareRules.Window(18, at + TimeSpan.FromHours(18).Ticks);
        Assert.Equal(TimeSpan.FromHours(18).Ticks, window.EndsAt - window.StartsAt);
        Assert.False(SovereigntyWarfareRules.Open(window, window.EndsAt));
        long deadline = SovereigntyWarfareRules.CampaignDeadline(at, 18, 1234);
        Assert.Equal(deadline, SovereigntyWarfareRules.CampaignDeadline(at, 18, 1234));
        var campaign = SovereigntyWarfareRules.Window(18, at + TimeSpan.FromDays(2).Ticks);
        Assert.InRange(deadline, campaign.StartsAt, campaign.EndsAt - 1);
    }

    [Fact]
    public void PaidIntervalsAndPassiveDefenseRespectExactBoundaries()
    {
        var result = SovereigntyWarfareRules.Advance(new(1, 0), [new(2, 0, 100)], 200, 1000, .2, 1, false);
        Assert.Equal(.92, result.Value, 9);
        var node = SovereigntyWarfareRules.Advance(new(0, 0), [], 10000, 100, .04, 1, true);
        Assert.Equal(2500, node.CompletedAt);
        Assert.Equal(1, node.Value);
        var closed = SovereigntyWarfareRules.Advance(new(1, 200), [new(2, 200, 1000)], 500, 1000, .2, 1, false, 100);
        Assert.Equal(1, closed.Value);
    }
}
