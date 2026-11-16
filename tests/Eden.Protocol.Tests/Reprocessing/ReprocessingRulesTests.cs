using Eden.Gameplay.Reprocessing;

namespace Eden.Protocol.Tests.Reprocessing;

public sealed class ReprocessingRulesTests
{
    [Theory]
    [InlineData(200, 115)]
    [InlineData(440, 253)]
    [InlineData(600, 345)]
    public void WholeUnitYieldsDoNotLoseMaterialToBinaryRounding(int materialQuantity, int expected)
    {
        var quote = ReprocessingRules.Quote(new(1, 1, 100, 100, 0.5, [1 + 5 * 3 / 100d], [new(34, materialQuantity)]));

        var material = Assert.Single(quote.Recoverables);
        Assert.Equal(expected, material.Quantity);
        Assert.Equal(materialQuantity - expected, material.Unrecoverable);
    }

    [Theory]
    [InlineData(0.999999999, 199)]
    [InlineData(0.575, 115)]
    [InlineData(0.574999999, 114)]
    [InlineData(0.575000001, 115)]
    public void FractionalYieldsStillRoundDownWithoutAnEpsilonBonus(double efficiency, int expected)
    {
        var quote = ReprocessingRules.Quote(new(1, 1, 100, 100, efficiency, [], [new(34, 200)]));

        Assert.Equal(expected, Assert.Single(quote.Recoverables).Quantity);
    }

    [Fact]
    public void SkillCombinationsAndWorldRatesPreserveDecimalYieldAndBatchRemainders()
    {
        const int materialPerPortion = 200;
        const int portions = 3;
        foreach (int reprocessing in Enumerable.Range(0, 6))
        {
            foreach (int efficiency in Enumerable.Range(0, 6))
            {
                foreach (int ore in Enumerable.Range(0, 6))
                {
                    foreach (decimal rate in new[] { 0.1m, 1m, 1.5m, 100m })
                    {
                        decimal yield = Math.Min(
                            1m,
                            0.5m * (1 + reprocessing * 0.03m) * (1 + efficiency * 0.02m) * (1 + ore * 0.02m) * rate
                        );
                        int expected = (int)decimal.Floor(materialPerPortion * portions * yield);
                        var quote = ReprocessingRules.Quote(
                            new(
                                1,
                                1,
                                333,
                                100,
                                0.5,
                                [1 + reprocessing * 0.03, 1 + efficiency * 0.02, 1 + ore * 0.02],
                                [new(34, materialPerPortion)],
                                rate
                            )
                        );

                        Assert.Equal(300, quote.QuantityToProcess);
                        Assert.Equal(33, quote.LeftOvers);
                        Assert.Equal(portions, quote.Portions);
                        var material = Assert.Single(quote.Recoverables);
                        Assert.Equal(expected, material.Quantity);
                        Assert.Equal(materialPerPortion * portions - expected, material.Unrecoverable);
                    }
                }
            }
        }
    }
}
