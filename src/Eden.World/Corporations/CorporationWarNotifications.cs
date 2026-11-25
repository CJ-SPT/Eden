using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Mail;
using Eden.Data.World.Structures;
using Eden.Gameplay.Notifications;
using Eden.World.Mail;
using Eden.World.Runtime.Economy;

namespace Eden.World.Corporations;

internal static class CorporationWarNotifications
{
    internal static void Declared(
        WorldAction world,
        InboxChangeSignal inbox,
        long warId,
        int attacker,
        int defender,
        long declaredAt,
        long startsAt,
        long? headquartersId
    )
    {
        var headquarters = headquartersId is long id ? world.Set<SavedStructure>().Find(id) : null;
        Add(
            world,
            inbox,
            InboxNotificationType.WarDeclared,
            attacker,
            new(warId, attacker, defender, startsAt, null, headquartersId, headquarters?.TypeId, headquarters?.Name),
            declaredAt,
            [attacker, defender]
        );
    }

    internal static void Ending(
        WorldAction world,
        InboxChangeSignal inbox,
        long warId,
        int attacker,
        int defender,
        long endingAt,
        long endsAt,
        bool mutual = false
    )
    {
        var type = mutual ? InboxNotificationType.WarMutualRetracted : InboxNotificationType.WarRetractedByConcord;
        Add(world, inbox, type, attacker, new(warId, attacker, defender, null, endsAt), endingAt, [attacker, defender]);
    }

    internal static void Invitation(WorldAction world, InboxChangeSignal inbox, int from, int to, long at, InboxNotificationType type)
    {
        int[] recipients = type switch
        {
            InboxNotificationType.WarMutualInvitation => [to],
            InboxNotificationType.WarMutualRejected => [from],
            _ => [from, to],
        };
        int sender = type == InboxNotificationType.WarMutualInvitation ? from : to;
        Add(world, inbox, type, sender, new(0, from, to, null, null), at, recipients);
    }

    private static void Add(
        WorldAction world,
        InboxChangeSignal inbox,
        InboxNotificationType type,
        int sender,
        CorporationWarNotificationContent content,
        long created,
        int[] owners
    )
    {
        HashSet<int> corporations = [.. owners];
        foreach (int owner in owners)
        {
            corporations.UnionWith(world.Filter<SavedAllianceMember>(m => m.AllianceId == owner).Select(m => m.CorporationId));
        }
        var recipients = corporations.SelectMany(c => world.MembershipChangeAudience(c, c).Recipients).ToHashSet();
        foreach (int corporation in corporations)
        {
            recipients.UnionWith(
                world
                    .Filter<SavedCorporationShareHolding>(s => s.CorporationId == corporation && s.Shares > 0)
                    .AsEnumerable()
                    .Select(s => s.ShareholderId)
                    .Where(world.PilotExists)
            );
        }
        string message = JsonSerializer.Serialize(content);
        foreach (long recipient in recipients.Order())
        {
            world.Add(
                new SavedInboxNotification
                {
                    Type = type,
                    SenderId = sender,
                    ReceiverId = recipient,
                    Created = created,
                    Message = message,
                }
            );
        }
        world.AfterCommit(inbox.Publish);
    }
}
