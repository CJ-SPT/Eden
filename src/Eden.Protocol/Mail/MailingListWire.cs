using Eden.Gameplay.Mail;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Mail;

internal sealed record MailingListReply(MailingListInfo List) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["id"] = List.Id,
                ["name"] = new BlueUnicode(List.Name.Key),
                ["displayName"] = new BlueUnicode(List.Name.Display),
                ["isMuted"] = List.Role == MailingListRole.Muted,
                ["isOperator"] = List.Role == MailingListRole.Operator,
                ["isOwner"] = List.Role == MailingListRole.Owner,
            }
        );
}

internal sealed record MailingListSettingsReply(MailingListSettings Settings) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["defaultAccess"] = (int)Settings.Defaults.Access,
                ["defaultMemberAccess"] = (int)Settings.Defaults.Role,
                ["mailCost"] = 0L,
                ["access"] = new BlueMap<long, int>(Settings.Access.ToDictionary(e => e.Key, e => (int)e.Value)),
            }
        );
}

internal sealed record MailingListWelcomeReply(MailingListWelcome Welcome) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?> { ["title"] = new BlueUnicode(Welcome.Title), ["body"] = new BlueUnicode(Welcome.Body) }
        );
}

internal sealed record MailingListRoleChanged(long ListId, MailingListRole Role)
    : PilotNotification(
        Role switch
        {
            MailingListRole.Muted => "OnMailingListSetMuted",
            MailingListRole.Operator => "OnMailingListSetOperator",
            _ => "OnMailingListSetClear",
        }
    )
{
    public override object?[] Arguments => [ListId];
}

internal sealed record MailingListLeft(long ListId, long PilotId) : PilotNotification("OnMailingListLeave")
{
    public override object?[] Arguments => [ListId, PilotId];
}

internal sealed record MailingListDeleted(long ListId) : PilotNotification("OnMailingListDeleted")
{
    public override object?[] Arguments => [ListId];
}
