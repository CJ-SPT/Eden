using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;

namespace Eden.Gameplay.Notifications;

public enum InboxNotificationType
{
    AllianceMaintenanceBill = 4,
    MilitiaCorporationStandingRemoval = 60,
    MilitiaCorporationStandingWarning = 62,
    ContactAdded = 89,
    ContactEdited = 90,
    MilitiaAllianceStandingWarning = 96,
    MilitiaAllianceStandingRemoval = 97,
    WarMercenaryOffer = 102,
    WarSurrenderOffer = 103,
    WarSurrenderDeclined = 104,
    WarAllyOfferDeclined = 110,
    KillRightAvailable = 115,
    KillRightAvailableOpen = 116,
    KillRightEarned = 117,
    KillRightUsed = 118,
    KillRightUnavailable = 119,
    KillRightUnavailableOpen = 120,
    WarSurrenderAccepted = 123,
    WarOfferedToAlly = 126,
    WarAllyAccepted = 127,

    // Build 3662168 eve.common.script.util.notificationconst.
    StructureItemsDelivered = 199,
    WarMutualInvitation = 229,
    WarMutualRejected = 230,
    WarMutualAccepted = 231,
    WarDeclared = 232,
    WarMutualRetracted = 238,
    WarRetractedByConcord = 239,
    WarMercenaryOfferRetracted = 241,
    WarOfferToAllyRetracted = 242,
    ExpertSystemExpired = 253,

    // Build 3662168 notifications.common.notificationconst.
    MilitiaMemberStandingWarning = 298,
    MilitiaCorporationCeoStandingWarning = 299,
    MilitiaAllianceCeoStandingWarning = 300,
    MilitiaMemberStandingRemoval = 301,
    MilitiaCorporationCeoStandingRemoval = 302,
    MilitiaAllianceCeoStandingRemoval = 303,
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

public sealed record KillRightNotificationContent(KillRight Right);

public sealed record WarNegotiationNotificationContent(int FromOwnerId, int ToOwnerId, long ActorId, long AmountCents);

public sealed record CorporationWarNotificationContent(
    long WarId,
    int AttackerId,
    int DefenderId,
    long? StartsAt,
    long? EndsAt,
    long? HeadquartersId = null,
    int? HeadquartersTypeId = null,
    string? HeadquartersName = null
)
{
    public WarNegotiationNotificationContent? Negotiation { get; init; }
}

public sealed record StructureDeliveryQuantity(int TypeId, long Quantity);

public sealed record StructureDeliveryContent(
    long StructureId,
    int StructureTypeId,
    int SystemId,
    StructureDeliveryQuantity[] Types,
    long[] ItemIds
);

public sealed record MilitiaStandingNotificationContent(
    long CharacterId,
    int CorporationId,
    int? AllianceId,
    MilitiaFaction Faction,
    double RequiredStanding,
    double Standing,
    long? WarningDuration
);

public sealed record InboxNotification(
    long Id,
    InboxNotificationType Type,
    long SenderId,
    long ReceiverId,
    bool Processed,
    bool Deleted,
    long Created,
    ContactNotificationContent Content,
    int? AllianceId = null,
    MilitiaStandingNotificationContent? MilitiaStanding = null,
    KillRightNotificationContent? KillRight = null
)
{
    public StructureDeliveryContent? StructureDelivery { get; init; }
    public int? ExpertSystemTypeId { get; init; }
    public CorporationWarNotificationContent? CorporationWar { get; init; }
}

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

    public static bool IsWar(InboxNotificationType type) =>
        type
            is >= InboxNotificationType.WarMutualInvitation
                and <= InboxNotificationType.WarDeclared
                or InboxNotificationType.WarMutualRetracted
                or InboxNotificationType.WarRetractedByConcord
                or (>= InboxNotificationType.WarMercenaryOffer and <= InboxNotificationType.WarSurrenderDeclined)
                or InboxNotificationType.WarAllyOfferDeclined
                or InboxNotificationType.WarSurrenderAccepted
                or InboxNotificationType.WarOfferedToAlly
                or InboxNotificationType.WarAllyAccepted
                or InboxNotificationType.WarMercenaryOfferRetracted
                or InboxNotificationType.WarOfferToAllyRetracted;

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
