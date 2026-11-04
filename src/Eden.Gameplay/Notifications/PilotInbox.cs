namespace Eden.Gameplay.Notifications;

public enum InboxNotificationType
{
    ContactAdded = 89,
    ContactEdited = 90,
}

// Build 3662168 notificationconst group IDs; group 13 is not assigned.
public enum InboxGroup
{
    Unread = 0,
    Agents = 1,
    Bills = 2,
    Corporation = 3,
    Miscellaneous = 4,
    Old = 5,
    Sovereignty = 6,
    Structures = 7,
    War = 8,
    Contacts = 9,
    Bounties = 10,
    Insurance = 11,
    Skills = 12,
    Official = 14,
    Raffle = 15,
    Insurgencies = 16,
    FactionWarfare = 17,
}

public sealed record ContactNotificationContent(double Level, string Message);

public sealed record InboxNotification(
    long Id,
    InboxNotificationType Type,
    long SenderId,
    long ReceiverId,
    bool Processed,
    bool Deleted,
    long Created,
    ContactNotificationContent Content
);

public sealed record InboxQuery(long AfterId = 0, InboxGroup? Group = null, bool UnreadOnly = false);

public sealed record InboxChanges(long Cursor, IReadOnlyList<InboxNotification> Items, bool More);

public interface IPilotInbox
{
    long Revision { get; }
    long Observe(long pilotId);
    InboxChanges Changes(long pilotId, long after);
    IReadOnlyList<InboxNotification> Read(long pilotId, InboxQuery query);
    void MarkRead(long pilotId, IReadOnlyList<long> ids);
    void MarkGroupRead(long pilotId, InboxGroup? group);
    void Delete(long pilotId, IReadOnlyList<long> ids);
    void DeleteGroup(long pilotId, InboxGroup? group);
}

public static class InboxRules
{
    // Native appConst.notificationsMaxUpdated and contactManagementWindow's edit.
    public const int MaximumUpdated = 100;
    public const int MaximumMessageLength = 120;
    public const int DeliveryBatchSize = 256;

    public static void CheckMessage(string message)
    {
        if (message.Length > MaximumMessageLength || message.Any(char.IsControl))
        {
            throw new InvalidDataException("Contact notification messages may contain up to 120 characters without control characters.");
        }
    }

    public static void CheckIds(IReadOnlyList<long> ids)
    {
        if (ids.Count > MaximumUpdated || ids.Any(id => id <= 0))
        {
            throw new InvalidDataException("Select at most one hundred valid notification IDs.");
        }
    }

    public static void CheckGroup(InboxGroup? group)
    {
        if (group.HasValue && !Enum.IsDefined(group.Value))
        {
            throw new InvalidDataException("Unknown notification group.");
        }
    }
}
