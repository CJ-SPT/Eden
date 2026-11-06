using Eden.Gameplay.Market;

namespace Eden.Protocol.Tests.Market;

public sealed class PersonalMarketRulesTests
{
    [Theory]
    [InlineData(true, 1200, 1000, false, 1200, 0)]
    [InlineData(false, 1500, 2000, false, 1500, 5000)]
    [InlineData(true, 1200, 1000, true, 1000, 2000)]
    [InlineData(false, 1500, 2000, true, 2000, 0)]
    public void MatchingPreservesOfferPricingPartialVolumeAndEscrowConservation(
        bool bid,
        long offer,
        long resting,
        bool npc,
        long price,
        long refund
    )
    {
        var match = PersonalMarketRules.Match(bid, offer, 20, resting, 10, npc);
        Assert.Equal(10, match.Quantity);
        Assert.Equal(price, match.PriceCents);
        Assert.Equal(10 * price, match.GrossCents);
        Assert.Equal(refund, match.RefundCents);
        Assert.Equal(match.ReservedCents, match.GrossCents + match.RefundCents);
    }

    [Fact]
    public void FeesUsePinnedSkillAndStandingInputsAndRelistDiscount()
    {
        Assert.Equal(0.03m, PersonalMarketRules.BrokerRate(0, 0, 0));
        Assert.Equal(0.01m, PersonalMarketRules.BrokerRate(5, 10, 10));
        Assert.Equal(0.035m, PersonalMarketRules.BrokerRate(0, -10, -10));
        Assert.Equal(10_000, PersonalMarketRules.BrokerFee(100, 1, 0.03m));
        Assert.Equal(300_000, PersonalMarketRules.BrokerFee(1_000_000, 10, 0.03m));
        Assert.Equal(132_000, PersonalMarketRules.RelistFee(1_000_000, 1_200_000, 10, 0.03m, 5));
        Assert.Equal(5, PersonalMarketRules.OrderLimit(0, 0, 0, 0));
        Assert.Equal(305, PersonalMarketRules.OrderLimit(5, 5, 5, 5));
        Assert.Throws<InvalidDataException>(() => PersonalMarketRules.Match(true, 100, 1, 200, 1, false));
        Assert.Throws<OverflowException>(() => PersonalMarketRules.Match(true, long.MaxValue, 2, 1, 2, false));
    }
}
