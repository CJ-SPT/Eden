using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Data.World.Mail;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Notifications;
using Eden.World.Mail;
using Eden.World.Runtime.Economy;

namespace Eden.World.Corporations;

internal sealed partial class CorporationMembershipStore
{
    private static void PublishRecruitmentInbox(
        WorldAction world,
        SavedCorporationApplication application,
        long[] recruiters,
        long? inviter
    )
    {
        string? response = string.IsNullOrWhiteSpace(application.Response) ? null : application.Response;
        var (type, toRecruiters) = application.Status switch
        {
            CorporationApplicationStatus.Applied => (InboxNotificationType.CorporationApplicationReceived, true),
            CorporationApplicationStatus.Joined => (InboxNotificationType.CorporationApplicationJoined, true),
            CorporationApplicationStatus.RejectedByApplicant => (InboxNotificationType.CorporationApplicationOfferDeclined, true),
            CorporationApplicationStatus.Withdrawn => (InboxNotificationType.CorporationApplicationWithdrawn, true),
            CorporationApplicationStatus.RejectedByCorporation when response is not null => (
                InboxNotificationType.CorporationApplicationDeclinedWithMessage,
                false
            ),
            CorporationApplicationStatus.RejectedByCorporation => (InboxNotificationType.CorporationApplicationDeclined, false),
            CorporationApplicationStatus.Offered => (InboxNotificationType.CorporationApplicationOffered, false),
            CorporationApplicationStatus.Invited => (InboxNotificationType.CorporationApplicationInvited, false),
            _ => ((InboxNotificationType?)null, false),
        };
        if (type is not { } notificationType)
        {
            return;
        }

        IEnumerable<long> recipients = toRecruiters ? recruiters.Where(id => id != application.PilotId) : [application.PilotId];
        var players = recipients.Where(id => world.Pilots.Find(id) is { AccountId: > 0 }).ToArray();
        if (players.Length == 0)
        {
            return;
        }
        string content = JsonSerializer.Serialize(
            new CorporationApplicationNotificationContent(
                application.PilotId,
                application.CorporationId,
                notificationType == InboxNotificationType.CorporationApplicationInvited ? null : application.Text,
                notificationType == InboxNotificationType.CorporationApplicationDeclinedWithMessage ? response : null,
                notificationType == InboxNotificationType.CorporationApplicationInvited ? inviter : null
            )
        );
        foreach (long recipient in players)
        {
            world.Add(
                new SavedInboxNotification
                {
                    Type = notificationType,
                    SenderId = toRecruiters ? application.PilotId : application.CorporationId,
                    ReceiverId = recipient,
                    Created = application.UpdatedAt,
                    Message = content,
                }
            );
        }
        PilotInboxStore.PublishResident(world);
    }
}
