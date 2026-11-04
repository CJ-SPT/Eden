using Eden.Gameplay.Contracts;

namespace Eden.Protocol.Tests.Persistence;

public sealed class AuctionRulesTests
{
    [Fact]
    public void CreationRetainsCentsAndExactBuyoutsWhileOrdinaryBidDefaultsUseWholeIsk()
    {
        var draft = AuctionTestData.Draft(100) with { PriceCents = 100_000_001, Auction = new(200_000_099) };
        ContractRules.Check(draft);
        Assert.Equal(100_000_000, AuctionRules.MinimumBid(draft.PriceCents, 0, draft.Auction!.BuyoutCents));
        AuctionRules.CheckBid(draft.PriceCents, 0, draft.Auction.BuyoutCents, draft.Auction.BuyoutCents);
        Assert.Throws<InvalidDataException>(() =>
            ContractRules.Check(draft with { PriceCents = AuctionRules.MinimumPublicStartingCents - 1 })
        );
        ContractRules.Check(draft with { PriceCents = 0, AssigneeId = AuctionTestData.Bidder });
        Assert.Throws<InvalidDataException>(() => ContractRules.Check(draft with { ExpiryMinutes = 28 * 1440 }));
    }

    [Theory]
    [InlineData(0, 0, 0, 100000)]
    [InlineData(10000, 0, 0, 100000)]
    [InlineData(1000000, 0, 0, 1000000)]
    [InlineData(1234500, 2000000, 0, 2123400)]
    [InlineData(1000000, 1200000, 1250000, 1250000)]
    [InlineData(0, 0, 10000, 10000)]
    public void MinimumBidUsesThePinnedWholeIskIncrementAndBuyoutCap(long start, long current, long buyout, long expected)
    {
        Assert.Equal(expected, AuctionRules.MinimumBid(start, current, buyout));
        AuctionRules.CheckBid(start, current, buyout, expected);
        Assert.Throws<InvalidDataException>(() => AuctionRules.CheckBid(start, current, buyout, expected - 1));
    }

    [Fact]
    public void RepeatedLateBidsExtendFromTheLatestBidWithoutShorteningALongerAuction()
    {
        long now = CalendarTestData.Clock().Now;
        long window = AuctionRules.ExtensionWindow.Ticks;
        long end = now + TimeSpan.FromHours(1).Ticks;
        Assert.Equal(end, AuctionRules.ExpiryAfterBid(now, end));
        Assert.Equal(end, AuctionRules.ExpiryAfterBid(end - window, end));
        long extended = AuctionRules.ExpiryAfterBid(end - 1, end);
        Assert.Equal(end - 1 + window, extended);
        Assert.Equal(extended - 1 + window, AuctionRules.ExpiryAfterBid(extended - 1, extended));
    }

    [Fact]
    public void AuctionTermsRejectDeliveryRewardsRequestedGoodsAndInvalidBuyouts()
    {
        var draft = AuctionTestData.Draft(100);
        ContractRules.Check(draft);
        foreach (
            var invalid in new[]
            {
                draft with
                {
                    Auction = null,
                },
                draft with
                {
                    Auction = new(-1),
                },
                draft with
                {
                    Auction = new(draft.PriceCents - 100),
                },
                draft with
                {
                    Auction = new(ContractRules.MaximumAmountCents + 100),
                },
                draft with
                {
                    PriceCents = 1,
                },
                draft with
                {
                    RewardCents = 100,
                },
                draft with
                {
                    Items = [],
                },
                draft with
                {
                    RequestedItems = [new(3651, 1)],
                },
                draft with
                {
                    Courier = new(60003760, 1, 0),
                },
                draft with
                {
                    Type = ContractType.ItemExchange,
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => ContractRules.Check(invalid));
        }
        Assert.Throws<InvalidDataException>(() => AuctionRules.CheckBid(0, 0, 0, 100001));
        Assert.Throws<InvalidDataException>(() => AuctionRules.CheckBid(0, 0, 200000, 200100));
    }
}
