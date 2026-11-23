namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Theory]
    [InlineData(909, 2, 7, 2, false)]
    [InlineData(910, 3, 8, 2, false)]
    [InlineData(1065, 4, 19, 6, true)]
    [InlineData(1084, 2, 8, 6, false)]
    public async Task SecurityMissionRequiresItsReportedTargetsAcrossCheckpoint(int id, int level, int total, int required, bool gated)
    {
        await VerifyReportedGroupCompletion(id, level, total, required, clearOptional: false, gated: gated);
    }
}
