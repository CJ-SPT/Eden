using Eden.Gameplay.Agents;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionItemRewardRuleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EachAuthoredAlternativeProducesExactlyTheQuotedItem(int selection)
    {
        int[] alternatives = [13283, 13284, 13285, 13287, 13286];
        Assert.Equal(new MissionItemReward(alternatives[selection], 1), MissionItemRewardRules.Quote(alternatives, 1, selection));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2)]
    public void InvalidQuantityOrSelectionRejects(int quantity, int selection) =>
        Assert.Throws<InvalidDataException>(() => MissionItemRewardRules.Quote([13283, 13284], quantity, selection));

    [Fact]
    public void MissingDuplicateAndInvalidAlternativesReject()
    {
        foreach (int[] alternatives in new int[][] { [], [0], [-1], [13283, 13283] })
        {
            Assert.Throws<InvalidDataException>(() => MissionItemRewardRules.Quote(alternatives, 1, 0));
        }
    }
}
