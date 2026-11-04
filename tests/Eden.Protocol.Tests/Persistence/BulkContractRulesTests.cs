using Eden.Gameplay.Contracts;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;

namespace Eden.Protocol.Tests.Persistence;

public sealed class BulkContractRulesTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void ThePinnedBulkSelectionLimitIsEnforcedAtBothEnds(int count, bool valid)
    {
        var draft = BulkContractTestData.Draft(Enumerable.Range(1, count).Select(i => (long)i).ToArray());
        if (valid)
        {
            ContractRules.Check(draft);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => ContractRules.Check(draft));
        }
    }

    [Fact]
    public void BulkTermsRequireWholeAssembledShipsWithoutRewardsRequestedItemsOrDuplicateSelections()
    {
        var draft = BulkContractTestData.Draft([100, 101]);
        foreach (
            var invalid in new[]
            {
                draft with
                {
                    RewardCents = 1,
                    PriceCents = 0,
                },
                draft with
                {
                    RequestedItems = [new(3651, 1)],
                },
                draft with
                {
                    Items = [new(100, 2)],
                },
                draft with
                {
                    Items = [new(100, 1), new(100, 1)],
                },
                draft with
                {
                    Type = ContractType.Courier,
                    Courier = new(60003760, 1, 0),
                    PriceCents = 0,
                },
                draft with
                {
                    Type = ContractType.Auction,
                    Auction = new(0),
                },
                draft with
                {
                    AssigneeId = null,
                    PriceCents = BulkContractRules.MinimumPublicPriceCents - 1,
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => ContractRules.Check(invalid));
        }
        ContractRules.Check(draft with { AssigneeId = null, PriceCents = BulkContractRules.MinimumPublicPriceCents });
        InventoryItem hull = new(100, 601, 1, 60003760, 4, 1, 237, 6, 15000, true);
        BulkContractRules.CheckShip(hull);
        foreach (
            var invalid in new[]
            {
                hull with
                {
                    Singleton = false,
                },
                hull with
                {
                    Quantity = 2,
                },
                hull with
                {
                    CategoryId = 7,
                },
                hull with
                {
                    GroupId = FleetDefinitions.CapsuleGroup,
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => BulkContractRules.CheckShip(invalid));
        }
    }
}
