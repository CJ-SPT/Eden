using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Targeting;

internal enum TargetChange
{
    Added,
    Lost,
    OtherAdded,
    OtherLost,
}

internal sealed record TargetChanged(TargetChange Change, long TargetId) : PilotNotification("OnTarget")
{
    public override object?[] Arguments =>
        [
            Change switch
            {
                TargetChange.Added => "add",
                TargetChange.Lost => "lost",
                TargetChange.OtherAdded => "otheradd",
                TargetChange.OtherLost => "otherlost",
                _ => throw new InvalidOperationException("Unknown target change."),
            },
            TargetId,
            null,
        ];
}

internal sealed record TargetLockReply(bool Pending, IReadOnlyList<long> Targets) : BlueModel
{
    // The RPC stays open until the scan succeeds or is cancelled. Only the
    // two native reply fields below are serialized; this task belongs to the session.
    internal Task<PilotCallResult>? Completion { get; init; }

    public override object ToBlue() => new object?[] { Pending, new BlueList<long>(Targets) };
}
