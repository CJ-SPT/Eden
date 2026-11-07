using Eden.Gameplay.Notifications;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Notifications;

[Injectable(InjectionType.Scoped)]
internal sealed class InboxHandler(IPilotInbox inbox) : ISelectedPilotCallHandler
{
    private long? _pilot;
    private long _cursor;
    private long _revision;
    private bool _more;
    private readonly HashSet<long> _fetched = [];
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_pilot == context.Pilot.PilotId)
        {
            return;
        }
        _revision = inbox.Revision;
        _cursor = inbox.Observe(context.Pilot.PilotId);
        _pilot = context.Pilot.PilotId;
        _more = false;
        _fetched.Clear();
    }

    public void Reset()
    {
        _pilot = null;
        _more = false;
        _fetched.Clear();
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        long revision = inbox.Revision;
        if (!_more && revision == _revision)
        {
            return;
        }
        var batch = inbox.Changes(context.Pilot.PilotId, _cursor);
        foreach (var notification in batch.Items)
        {
            bool fetched = _fetched.Remove(notification.Id);
            if (!fetched && !notification.Processed && !notification.Deleted)
            {
                context.Notifications.Add(new InboxNotificationReceived(notification));
            }
        }
        _cursor = batch.Cursor;
        _revision = revision;
        _more = batch.More;
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "notificationMgr"
            || method
                is not (
                    "GetAllNotifications"
                    or "GetUnprocessed"
                    or "GetByGroupID"
                    or "MarkAsProcessed"
                    or "MarkAllAsProcessed"
                    or "MarkGroupAsProcessed"
                    or "DeleteNotifications"
                    or "DeleteAllNotifications"
                    or "DeleteGroupNotifications"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            Enter(context);
            if (
                keywords is { Count: > 0 }
                && (method != "GetAllNotifications" || keywords.Count != 1 || !keywords.ContainsKey("fromID") || args.Length != 0)
            )
            {
                throw new InvalidDataException("Unexpected notification arguments.");
            }
            long pilot = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetAllNotifications", [] or [_]):
                    object? cutoff = args.Length == 1 ? args[0] : keywords?.GetValueOrDefault("fromID");
                    long after = cutoff switch
                    {
                        null => 0,
                        long id when id >= 0 => id,
                        _ => throw new InvalidDataException("Choose a valid notification history cutoff."),
                    };
                    return Rows(pilot, new(AfterId: after));
                case ("GetUnprocessed", []):
                    return Rows(pilot, new(UnreadOnly: true));
                case ("GetByGroupID", [long group]):
                    return Rows(pilot, new(Group: Group(group)));
                case ("MarkAsProcessed", [var ids]):
                    inbox.MarkRead(pilot, Ids(ids));
                    return PilotCallResult.None;
                case ("MarkAllAsProcessed", []):
                    inbox.MarkGroupRead(pilot, null);
                    return PilotCallResult.None;
                case ("MarkGroupAsProcessed", [long group]):
                    inbox.MarkGroupRead(pilot, Group(group));
                    return PilotCallResult.None;
                case ("DeleteNotifications", [var ids]):
                    inbox.Delete(pilot, Ids(ids));
                    return PilotCallResult.None;
                case ("DeleteAllNotifications", []):
                    inbox.DeleteGroup(pilot, null);
                    return PilotCallResult.None;
                case ("DeleteGroupNotifications", [long group]):
                    inbox.DeleteGroup(pilot, Group(group));
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid notification request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private BlueList<InboxNotificationReply> Rows(long pilot, InboxQuery query)
    {
        var notifications = inbox.Read(pilot, query);
        // A history/unread fetch can win the race with the periodic push. Do not
        // insert those same IDs into the client's caches a second time.
        foreach (var notification in notifications.Where(n => n.Id > _cursor))
        {
            _fetched.Add(notification.Id);
        }
        return new(notifications.Select(n => new InboxNotificationReply(n)).ToArray());
    }

    private static InboxGroup Group(long value) =>
        value is >= int.MinValue and <= int.MaxValue && Enum.IsDefined((InboxGroup)value)
            ? (InboxGroup)value
            : throw new InvalidDataException("Unknown notification group.");

    private static IReadOnlyList<long> Ids(object? value) =>
        value is List<object?> list && list.Count <= InboxRules.MaximumUpdated && list.All(id => id is long and > 0)
            ? list.Cast<long>().ToArray()
            : throw new InvalidDataException("Select a list of at most one hundred notification IDs.");
}

internal sealed record InboxContactData(ContactNotificationContent Content) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?> { ["level"] = Content.Level, ["message"] = new BlueUnicode(Content.Message) };
}

internal sealed record InboxNotificationReply(InboxNotification Notification) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["notificationID"] = Notification.Id,
                ["typeID"] = (int)Notification.Type,
                ["senderID"] = Notification.SenderId,
                ["receiverID"] = Notification.ReceiverId,
                ["processed"] = Notification.Processed,
                ["created"] = Notification.Created,
                ["data"] = new InboxContactData(Notification.Content),
            }
        );
}

internal sealed record InboxNotificationReceived(InboxNotification Notification) : PilotNotification("OnNotificationReceived")
{
    public override object?[] Arguments =>
        [Notification.Id, (int)Notification.Type, Notification.SenderId, Notification.Created, new InboxContactData(Notification.Content)];
}
