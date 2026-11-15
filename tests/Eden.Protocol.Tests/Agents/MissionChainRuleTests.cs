using Eden.Gameplay.Agents;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionChainRuleTests
{
    private const int First = 101;
    private const int Middle = 102;
    private const int Last = 103;
    private const int Unreachable = 104;

    [Theory]
    [InlineData("missing-root")]
    [InlineData("missing-middle")]
    [InlineData("missing-last")]
    [InlineData("cycle")]
    [InlineData("wrong-predecessor")]
    [InlineData("different-level")]
    [InlineData("different-lifecycle")]
    [InlineData("unreachable")]
    [InlineData("duplicate")]
    public void ChainAdmissionRejectsIncompleteAmbiguousOrCyclicTopology(string fault)
    {
        MissionChainNode[] valid =
        [
            new(First, 1, false, new(First, 0, Middle)),
            new(Middle, 1, false, new(First, First, Last)),
            new(Last, 1, false, new(First, Middle, 0)),
        ];
        MissionChainRules.Validate(valid);
        var invalid = fault switch
        {
            "missing-root" => valid[1..],
            "missing-middle" => [valid[0], valid[2]],
            "missing-last" => valid[..^1],
            "cycle" => [valid[0], valid[1], valid[2] with { Chain = new(First, Middle, First) }],
            "wrong-predecessor" => [valid[0], valid[1] with { Chain = new(First, 0, Last) }, valid[2]],
            "different-level" => [valid[0], valid[1] with { Level = 2 }, valid[2]],
            "different-lifecycle" => [valid[0], valid[1] with { Storyline = true }, valid[2]],
            "unreachable" => [.. valid, new(Unreachable, 1, false, new(First, Last, 0))],
            "duplicate" => [.. valid, valid[1]],
            _ => throw new InvalidOperationException("Unknown topology fault."),
        };
        Assert.Throws<InvalidDataException>(() => MissionChainRules.Validate(invalid));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(Last, MissionState.Completed, false)]
    [InlineData(First, MissionState.Accepted, false)]
    [InlineData(First, MissionState.Declined, false)]
    [InlineData(First, MissionState.Failed, false)]
    [InlineData(First, MissionState.Quit, false)]
    [InlineData(First, MissionState.Completed, true)]
    public void OnlyTheCompletedImmediatePredecessorAuthorizesANextPart(int? mission, MissionState? state, bool allowed)
    {
        MissionChainPart next = new(First, First, Last);
        if (allowed)
        {
            MissionChainRules.RequirePredecessor(next, mission, state);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => MissionChainRules.RequirePredecessor(next, mission, state));
        }
        MissionChainRules.RequirePredecessor(new(First, 0, Middle), mission, state);
    }
}
