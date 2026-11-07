using System.Globalization;
using System.Text.Json;
using Eden.Gameplay.Mail;
using Eden.Gameplay.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Mail;

public sealed class SavedInboxNotification
{
    public long Id { get; set; }
    public InboxNotificationType Type { get; set; }
    public long SenderId { get; set; }
    public long ReceiverId { get; set; }
    public bool Processed { get; set; }
    public bool Deleted { get; set; }
    public long Created { get; set; }
    public double ContactLevel { get; set; }
    public string Message { get; set; } = "";

    public InboxNotification Snapshot() => new(Id, Type, SenderId, ReceiverId, Processed, Deleted, Created, new(ContactLevel, Message));
}

public sealed class SavedMail
{
    public long Id { get; set; }

    // Historical headers may outlive their sender; mailbox ownership has its own FK.
    public long SenderId { get; set; }
    public long SentAt { get; set; }
    public long? ListId { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string RecipientsJson { get; set; } = "[]";

    public MailHeader Header() =>
        new(Id, SenderId, SentAt, JsonSerializer.Deserialize<long[]>(RecipientsJson)!, Subject) { ListId = ListId };
}

public sealed class SavedMailChange
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long MessageId { get; set; }
    public Guid Origin { get; set; }
    public MailChangeKind Kind { get; set; }
    public MailStatus Status { get; set; }
    public long Labels { get; set; }
    public long LabelId { get; set; }
}

[PrimaryKey(nameof(PilotId), nameof(MessageId))]
public sealed class SavedMailEntry
{
    public long PilotId { get; set; }
    public long MessageId { get; set; }
    public MailStatus Status { get; set; }
    public long Labels { get; set; }
    public bool Deleted { get; set; }

    public MailEntryState Snapshot() => new(MessageId, Status, Labels);
}

[PrimaryKey(nameof(PilotId), nameof(Id))]
public sealed class SavedMailLabel
{
    public long PilotId { get; set; }
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int? Color { get; set; }

    public MailLabel Snapshot() => new(Id, Name, Color);
}

public sealed class SavedMailingList
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public MailingListAccess DefaultAccess { get; set; }
    public MailingListRole DefaultRole { get; set; }
    public bool Deleted { get; set; }
    public string? WelcomeTitle { get; set; }
    public string? WelcomeBody { get; set; }

    public MailingListInfo Info(MailingListRole role) => new(Id, new(Key, Name), role);
}

[PrimaryKey(nameof(ListId), nameof(EntityId))]
public sealed class SavedMailingListAccess
{
    public long ListId { get; set; }
    public long EntityId { get; set; }
    public MailingListAccess Access { get; set; }
}

public sealed class SavedMailingListChange
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long ListId { get; set; }
    public Guid Origin { get; set; }
    public MailingListChangeKind Kind { get; set; }
    public MailingListRole Role { get; set; }
}

[PrimaryKey(nameof(ListId), nameof(PilotId))]
public sealed class SavedMailingListMember
{
    public long ListId { get; set; }
    public long PilotId { get; set; }
    public MailingListRole Role { get; set; }
}
