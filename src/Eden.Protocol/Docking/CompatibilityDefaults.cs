using Eden.Gameplay.Combat;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

// These records preserve the implemented empty compatibility states. They do not
// define schemas or behavior for contacts, fighters, clones, or campaigns.
internal sealed record BountiesAndKillRightsState() : BlueModel
{
    public override object ToBlue() => new object?[] { new List<object?>(), new List<object?>() };
}

internal sealed record BountyReply(long TargetId) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            TargetId,
            new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["targetID"] = TargetId, ["bounty"] = 0d }),
        };
}

internal sealed record LocalSystemState(int SystemId) : BlueModel
{
    public override object ToBlue() => new object?[] { SystemId, null };
}

internal sealed record DynamicBountyState() : BlueModel
{
    public override object ToBlue() => new object?[] { 1d, false };
}

internal sealed record CrimewatchClientState(ShipSafetyLevel Safety) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Enumerable.Range(0, 5).Select(_ => (object?)new object?[] { null, null }).ToArray(),
            new Dictionary<string, object?>(),
            new object?[] { new List<object?>(), new List<object?>() },
            (int)Safety,
        };
}

internal sealed record JumpTimerState() : BlueModel
{
    public override object ToBlue() => new object?[] { null, null, null };
}

internal sealed record FighterState() : BlueModel
{
    public override object ToBlue() => new object?[] { new List<object?>(), new List<object?>(), new Dictionary<string, object?>() };
}

internal sealed record AgentJournalState() : BlueModel
{
    public override object ToBlue() => new object?[] { new List<object?>(), new List<object?>() };
}
