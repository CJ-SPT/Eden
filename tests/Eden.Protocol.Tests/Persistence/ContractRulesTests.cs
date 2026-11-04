using Eden.Gameplay.Contracts;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ContractRulesTests
{
    [Theory]
    [InlineData(1000, 0, 1, 10000, 100000, 10000)]
    [InlineData(100000000, 0, 1, 400000, 1000000, 1000000)]
    [InlineData(100000000, 5, 1, 300000, 1000000, 500000)]
    [InlineData(100000000, 0, 28, 940000, 1000000, 1000000)]
    [InlineData(10000000000000, 5, 28, 176250000, 100000000, 500000000)]
    public void PublicFeeQuotesMatchPinnedMinimumsSkillReductionsCapsAndDuration(
        long priceIsk,
        int level,
        int days,
        long brokerIsk,
        long depositIsk,
        long taxIsk
    )
    {
        var draft = ContractTests.Draft(1, null) with { PriceCents = priceIsk * 100, ExpiryMinutes = days * 1440 };
        Assert.Equal(
            new ContractFees(brokerIsk * 100, depositIsk * 100, taxIsk * 100),
            ContractRules.Fees(draft, new(level, level, level, level))
        );
        Assert.Equal(1 + 8 * level, ContractRules.Limit(new(level, level, level, level)));
        Assert.Equal(
            new ContractFees(1000000, 0, 0),
            ContractRules.Fees(draft with { AssigneeId = ContractAssetTestData.Other }, new(level, level, level, level))
        );
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0.001)]
    [InlineData(10000000000001)]
    public void InvalidFinancialValuesAreRejected(double amount) => Assert.Throws<InvalidDataException>(() => ContractRules.Cents(amount));

    [Fact]
    public void DraftLimitsRejectInvalidQuantitiesDuplicatesTextAndConflictingPayments()
    {
        var draft = ContractTests.Draft(1);
        foreach (
            var invalid in new[]
            {
                draft with
                {
                    Items = [],
                },
                draft with
                {
                    Items = [new(1, 0)],
                },
                draft with
                {
                    Items = [new(1, 1), new(1, 2)],
                },
                draft with
                {
                    RequestedItems = [new(3651, 1), new(3651, 1)],
                },
                draft with
                {
                    AssigneeId = 0,
                },
                draft with
                {
                    PriceCents = -1,
                },
                draft with
                {
                    RewardCents = 1,
                },
                draft with
                {
                    ExpiryMinutes = 1439,
                },
                draft with
                {
                    Title = new('x', 51),
                },
                draft with
                {
                    Description = new('x', 4001),
                },
                draft with
                {
                    Title = "\0",
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => ContractRules.Check(invalid));
        }
        ContractRules.Check(draft with { Title = new('x', 50), Description = new('x', 4000) });
        Assert.Equal(30000, ContractRules.Cents(300.00000000000006));
        Assert.Equal(ContractRules.MaximumAmountCents, ContractRules.Cents(10000000000000));
    }

    [Theory]
    [InlineData(-0.1, ContractSecurityClass.Null)]
    [InlineData(0, ContractSecurityClass.Null)]
    [InlineData(0.449999, ContractSecurityClass.Low)]
    [InlineData(0.45, ContractSecurityClass.High)]
    [InlineData(0.949999, ContractSecurityClass.High)]
    [InlineData(0.95, ContractSecurityClass.Safe)]
    public void SearchSecurityClassesUseTheNativeThresholds(double value, ContractSecurityClass expected) =>
        Assert.Equal(expected, ContractSearchRules.SecurityClass(value));
}
