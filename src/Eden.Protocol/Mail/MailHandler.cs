using Eden.Gameplay.Mail;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Mail;

[Injectable(InjectionType.Scoped)]
internal sealed class MailHandler(IPilotMail mail, TimeProvider clock) : ISelectedPilotCallHandler
{
    private readonly Guid _origin = Guid.NewGuid();
    private long? _pilot;
    private long _cursor;
    private long _nextPoll;
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_pilot != context.Pilot.PilotId)
        {
            _pilot = context.Pilot.PilotId;
            _cursor = mail.Observe(_pilot.Value);
            _nextPoll = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        }
    }

    public void Reset() => _pilot = null;

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (now < _nextPoll)
        {
            return;
        }
        var batch = mail.Changes(context.Pilot.PilotId, _cursor);
        _cursor = batch.Cursor;
        _nextPoll = batch.More ? now : checked(now + TimeSpan.TicksPerSecond);
        foreach (var change in batch.Changes.Where(c => c.Origin != _origin))
        {
            context.Notifications.Add(
                change.Kind switch
                {
                    MailChangeKind.Sent => new MailSent(change.Header!, change.State),
                    MailChangeKind.State => new MailStateChanged(change.State),
                    MailChangeKind.Trashed => new MailTrashed(change.State.Id),
                    MailChangeKind.Restored => new MailRestored(change.State.Id),
                    MailChangeKind.Deleted => new MailDeleted(change.State.Id),
                    MailChangeKind.Label => new MailLabelChanged(change.Label!),
                    _ => throw new InvalidDataException("Unknown mail event."),
                }
            );
        }
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "mailingListsMgr" && method == "GetJoinedLists" && args.Length == 0 && keywords is null or { Count: 0 })
        {
            // Eden has no created/joined mailing lists. This read lets personal
            // mail initialize without inventing a group membership.
            return BlueEmpty.Dictionary;
        }
        if (
            service != "mailMgr"
            || method
                is not (
                    "SyncMail"
                    or "GetMailHeaders"
                    or "GetBody"
                    or "SendMail"
                    or "GetLabels"
                    or "CreateLabel"
                    or "EditLabel"
                    or "DeleteLabel"
                    or "MarkAsRead"
                    or "MarkAllAsRead"
                    or "MarkAsReadByLabel"
                    or "MarkAsReadByList"
                    or "MarkAsUnread"
                    or "MarkAllAsUnread"
                    or "MarkAsUnreadByLabel"
                    or "MarkAsUnreadByList"
                    or "MoveToTrash"
                    or "MoveAllToTrash"
                    or "MoveToTrashByLabel"
                    or "MoveToTrashByList"
                    or "MoveFromTrash"
                    or "MoveAllFromTrash"
                    or "DeleteMail"
                    or "EmptyTrash"
                    or "AssignLabels"
                    or "RemoveLabels"
                    or "ReplaceLabels"
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
                && (
                    method != "SendMail"
                    || keywords.Count != 1
                    || !keywords.TryGetValue("approvedCost", out var cost)
                    || cost is not (null or 0L or 0d)
                )
            )
            {
                throw new InvalidDataException("Unexpected mail arguments or stale CSPA approval.");
            }
            long pilot = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("SyncMail", [var first, long last]):
                    return new MailSyncReply(mail.Sync(pilot, OptionalId(first), last));
                case ("GetMailHeaders", [List<object?> ids]):
                    return new BlueList<MailHeaderReply>(mail.Headers(pilot, Ids(ids)).Select(h => new MailHeaderReply(h)).ToArray());
                case ("GetBody", [long id, bool read]) when id > 0:
                    return new MailBodyReply(mail.Body(pilot, id, read, _origin));
                case ("SendMail", [List<object?> recipients, var list, var corporation, var subject, var body, var reply, var forward]):
                    if (OptionalId(list).HasValue || OptionalId(corporation).HasValue)
                    {
                        throw new InvalidDataException("Mailing-list and corporation/alliance sends are not available.");
                    }
                    return mail.Send(
                        pilot,
                        new(Ids(recipients), Text(subject), Text(body), OptionalId(reply), OptionalId(forward)),
                        _origin
                    );
                case ("GetLabels", []):
                    return new BlueMap<long, MailLabelReply>(mail.Labels(pilot).ToDictionary(l => l.Id, l => new MailLabelReply(l)));
                case ("CreateLabel", [var name, var color]):
                    return mail.CreateLabel(pilot, Text(name), Color(color), _origin);
                case ("EditLabel", [long label, var name, var color]):
                    mail.EditLabel(pilot, label, name is null ? null : Text(name), Color(color), _origin);
                    return PilotCallResult.None;
                case ("DeleteLabel", [long label]):
                    mail.DeleteLabel(pilot, label, _origin);
                    return PilotCallResult.None;
            }
            MailOperation? operation = method switch
            {
                "MarkAsRead" or "MarkAllAsRead" or "MarkAsReadByLabel" or "MarkAsReadByList" => MailOperation.Read,
                "MarkAsUnread" or "MarkAllAsUnread" or "MarkAsUnreadByLabel" or "MarkAsUnreadByList" => MailOperation.Unread,
                "MoveToTrash" or "MoveAllToTrash" or "MoveToTrashByLabel" or "MoveToTrashByList" => MailOperation.Trash,
                "MoveFromTrash" or "MoveAllFromTrash" => MailOperation.Restore,
                "DeleteMail" or "EmptyTrash" => MailOperation.Delete,
                "AssignLabels" => MailOperation.AssignLabels,
                "RemoveLabels" => MailOperation.RemoveLabels,
                "ReplaceLabels" => MailOperation.ReplaceLabels,
                _ => null,
            };
            if (!operation.HasValue)
            {
                throw new InvalidDataException("Invalid mail request arguments.");
            }
            if (method.EndsWith("ByList", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Mailing lists are not available.");
            }
            long labels = 0;
            MailSelection selection = (method, args) switch
            {
                ("AssignLabels" or "RemoveLabels" or "ReplaceLabels", [List<object?> ids, long mask]) => WithLabels(ids, mask),
                (_, [List<object?> ids]) => new MailSelection.Ids(Ids(ids)),
                (_, [long mask]) when method.EndsWith("ByLabel", StringComparison.Ordinal) => new MailSelection.Label(mask),
                ("MoveAllFromTrash" or "EmptyTrash", []) => new MailSelection.Trash(),
                ("MarkAllAsRead" or "MarkAllAsUnread" or "MoveAllToTrash", []) => new MailSelection.All(),
                _ => throw new InvalidDataException("Invalid mailbox operation arguments."),
            };
            mail.Change(pilot, selection, operation.Value, labels, _origin);
            return PilotCallResult.None;

            MailSelection WithLabels(List<object?> ids, long mask)
            {
                labels = mask;
                return new MailSelection.Ids(Ids(ids));
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected mail text.");

    private static IReadOnlyList<long> Ids(List<object?> values) =>
        values.Select(v => v is long id && id > 0 ? id : throw new InvalidDataException("Expected a positive mail or pilot ID.")).ToArray();

    private static long? OptionalId(object? value) =>
        value switch
        {
            // Stock NewNewMessage initializes both source fields to Python False.
            null or false or 0L => null,
            long id when id > 0 => id,
            _ => throw new InvalidDataException("Expected a mail identifier or None."),
        };

    private static int? Color(object? value) =>
        value switch
        {
            null => null,
            long color when color is >= 0 and <= int.MaxValue => (int)color,
            _ => throw new InvalidDataException("Invalid mail label color."),
        };
}
