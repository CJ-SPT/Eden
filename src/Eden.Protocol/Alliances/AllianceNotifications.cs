using Eden.Gameplay.Alliances;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Alliances;

internal sealed record AllianceChanged(AllianceChange Change) : PilotNotification("OnAllianceChanged")
{
    public override object?[] Arguments =>
        [
            Change.AllianceId,
            AllianceNotificationFields.Delta(
                Change.Before is null ? null : (BlueConstructor)new AllianceReply(Change.Before).ToBlue(),
                Change.After is null ? null : (BlueConstructor)new AllianceReply(Change.After).ToBlue()
            ),
        ];
}

internal sealed record AllianceMemberChanged(AllianceChange Change) : PilotNotification("OnAllianceMemberChanged")
{
    public override object?[] Arguments =>
        [
            Change.AllianceId,
            Change.CorporationId,
            AllianceNotificationFields.Delta(Member(Change.MemberBefore), Member(Change.MemberAfter)),
        ];

    private static BlueConstructor? Member(AllianceMember? member) =>
        member is null
            ? null
            : (BlueConstructor)((BlueIndexedRowset)new AllianceMembersReply([member]).ToBlue()).Rows[member.CorporationId]!;
}

internal sealed record AllianceApplicationChanged(AllianceChange Change) : PilotNotification("OnAllianceApplicationChanged")
{
    public override object?[] Arguments =>
        [
            Change.AllianceId,
            Change.CorporationId,
            AllianceNotificationFields.Delta(
                Change.ApplicationBefore is null ? null : (BlueConstructor)new AllianceApplicationRow(Change.ApplicationBefore).ToBlue(),
                Change.ApplicationAfter is null ? null : (BlueConstructor)new AllianceApplicationRow(Change.ApplicationAfter).ToBlue()
            ),
        ];
}

internal static class AllianceNotificationFields
{
    internal static Dictionary<string, object?> Delta(BlueConstructor? before, BlueConstructor? after)
    {
        var descriptor = (BlueConstructor)(after ?? before)!.Arguments[0]!;
        var columns = (object?[])descriptor.Arguments[0]!;
        var old = before?.Arguments[1] as object?[];
        var current = after?.Arguments[1] as object?[];
        Dictionary<string, object?> fields = [];
        for (int index = 0; index < columns.Length; index++)
        {
            object? previous = old?[index];
            object? next = current?[index];
            if (!Equals(previous, next) || before is null || after is null)
            {
                fields[(string)((object?[])columns[index]!)[0]!] = new object?[] { previous, next };
            }
        }
        return fields;
    }
}
