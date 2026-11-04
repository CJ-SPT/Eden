using System.Text.RegularExpressions;

namespace Eden.Gameplay.Mail;

[Flags]
public enum MailStatus
{
    None = 0,
    Read = 1,
    Replied = 2,
    Forwarded = 4,
    Trashed = 8,
    Draft = 16,
    Automated = 32,
}

[Flags]
public enum SystemMailLabel
{
    Inbox = 1,
    Sent = 2,
    Corporation = 4,
    Alliance = 8,
}

public sealed record MailDraft(IReadOnlyList<long> Recipients, string Subject, string Body, long? ReplyTo, long? ForwardedFrom);

public sealed record MailHeader(long Id, long SenderId, long SentAt, IReadOnlyList<long> Recipients, string Subject);

public sealed record MailEntryState(long Id, MailStatus Status, long Labels);

public sealed record MailSnapshot(IReadOnlyList<MailHeader> New, IReadOnlyList<MailHeader> Old, IReadOnlyList<MailEntryState> Status);

public sealed record MailLabel(long Id, string Name, int? Color);

public abstract record MailSelection
{
    public sealed record Ids(IReadOnlyList<long> Values) : MailSelection;

    public sealed record All : MailSelection;

    public sealed record Label(long Mask) : MailSelection;

    public sealed record Trash : MailSelection;
}

public enum MailOperation
{
    Read,
    Unread,
    Trash,
    Restore,
    Delete,
    AssignLabels,
    RemoveLabels,
    ReplaceLabels,
}

public enum MailChangeKind
{
    Sent,
    State,
    Trashed,
    Restored,
    Deleted,
    Label,
}

public sealed record MailChange(long Id, Guid Origin, MailChangeKind Kind, MailEntryState State, MailHeader? Header, MailLabel? Label);

public sealed record MailChangeBatch(long Cursor, bool More, IReadOnlyList<MailChange> Changes);

public interface IPilotMail
{
    long Observe(long pilotId);
    MailChangeBatch Changes(long pilotId, long cursor);
    MailSnapshot Sync(long pilotId, long? first, long last);
    IReadOnlyList<MailHeader> Headers(long pilotId, IReadOnlyList<long> ids);
    string Body(long pilotId, long messageId, bool markRead, Guid origin);
    long Send(long pilotId, MailDraft draft, Guid origin);
    void Change(long pilotId, MailSelection selection, MailOperation operation, long labels, Guid origin);
    IReadOnlyList<MailLabel> Labels(long pilotId);
    long CreateLabel(long pilotId, string name, int? color, Guid origin);
    void EditLabel(long pilotId, long labelId, string? name, int? color, Guid origin);
    void DeleteLabel(long pilotId, long labelId, Guid origin);
}

public static partial class MailRules
{
    // Build 3662168 appConst mail limits and native signed-integer label masks.
    public const int MaximumRecipients = 50;
    public const int MaximumSubject = 150;
    public const int MaximumBody = 8000;
    public const int MaximumTaggedBody = 10000;
    public const int MaximumLabelName = 40;
    public const int MaximumLabels = 25;
    public const int MaximumPerMinute = 5;
    public const int FeedBatch = 128;
    public const long SystemLabels = 15;
    public const long FirstCustomLabel = 16;
    public const long MaximumMask = (FirstCustomLabel << MaximumLabels) - 1;

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    public static void Draft(MailDraft draft)
    {
        if (
            draft.Recipients.Count is < 1 or > MaximumRecipients
            || draft.Recipients.Any(id => id <= 0)
            || draft.Recipients.Distinct().Count() != draft.Recipients.Count
        )
        {
            throw new InvalidDataException("Choose up to 50 distinct local pilot recipients.");
        }
        if (string.IsNullOrWhiteSpace(draft.Subject) || draft.Subject.Length > MaximumSubject || draft.Subject.Contains('\0'))
        {
            throw new InvalidDataException("Mail subjects need 1 to 150 characters.");
        }
        if (draft.Body.Length > MaximumTaggedBody || Tags().Replace(draft.Body, "").Length > MaximumBody || draft.Body.Contains('\0'))
        {
            throw new InvalidDataException("Mail exceeds the body size limit.");
        }
        if (draft.ReplyTo <= 0 || draft.ForwardedFrom <= 0 || (draft.ReplyTo.HasValue && draft.ForwardedFrom.HasValue))
        {
            throw new InvalidDataException("Choose one valid reply or forward source.");
        }
    }

    public static void Label(string name, int? color)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumLabelName || name.Contains('\0') || color < 0)
        {
            throw new InvalidDataException("Mail labels need a name of 1 to 40 characters and a valid color.");
        }
    }

    public static void LabelId(long id)
    {
        if (id <= 0 || id > MaximumMask || (id & (id - 1)) != 0)
        {
            throw new InvalidDataException("Choose one mail label.");
        }
    }

    public static MailEntryState Change(MailEntryState before, MailOperation operation, long mask) =>
        operation switch
        {
            MailOperation.Read => before with { Status = before.Status | MailStatus.Read },
            MailOperation.Unread => before with { Status = before.Status & ~MailStatus.Read },
            MailOperation.Trash => before with { Status = before.Status | MailStatus.Trashed },
            MailOperation.Restore => before with { Status = before.Status & ~MailStatus.Trashed },
            MailOperation.Delete when before.Status.HasFlag(MailStatus.Trashed) => before,
            MailOperation.AssignLabels => before with { Labels = before.Labels | mask },
            MailOperation.RemoveLabels => before with { Labels = before.Labels & ~mask },
            MailOperation.ReplaceLabels => before with { Labels = mask },
            _ => throw new InvalidDataException("Only trashed mail can be permanently deleted."),
        };
}
