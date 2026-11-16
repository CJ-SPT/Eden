namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Theory]
    [InlineData(888, 2, 10, 4, false)]
    [InlineData(888, 2, 10, 4, true)]
    [InlineData(898, 3, 14, 8, false)]
    [InlineData(898, 3, 14, 8, true)]
    [InlineData(902, 2, 11, 2, false)]
    [InlineData(902, 2, 11, 2, true)]
    [InlineData(901, 3, 11, 2, false)]
    [InlineData(901, 3, 11, 2, true)]
    [InlineData(900, 4, 19, 4, false)]
    [InlineData(900, 4, 19, 4, true)]
    public async Task SpyBlitzRequiresEveryReportedTargetAndPreservesOptionalGuardsAcrossCheckpoint(
        int id,
        int level,
        int total,
        int requiredCount,
        bool clearOptional
    )
    {
        await VerifyReportedGroupCompletion(id, level, total, requiredCount, clearOptional);
    }
}
