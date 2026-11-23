using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Notifications;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Notifications;

[Injectable(InjectionType.Scoped)]
internal sealed class InboxHandler(IPilotInbox inbox, IPilotInventory inventory) : ISelectedPilotCallHandler
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
        var pilot = context.Pilot;
        HashSet<long>? deliveredItems = null;
        foreach (var notification in batch.Items)
        {
            if (notification.KillRight is { Right: var right })
            {
                switch (notification.Type)
                {
                    case InboxNotificationType.KillRightEarned:
                        context.Notifications.Add(new KillRightCreated(right));
                        break;
                    case InboxNotificationType.KillRightUsed:
                        context.Notifications.Add(new KillRightUsed(right));
                        break;
                    case InboxNotificationType.KillRightAvailable:
                    case InboxNotificationType.KillRightAvailableOpen:
                        context.Notifications.Add(new KillRightListed(right.Id));
                        break;
                }
            }
            bool fetched = _fetched.Remove(notification.Id);
            // Earned and used events also invalidate the target's client cache. The inbox row belongs to the right owner.
            if (notification.ReceiverId == _pilot && !fetched && !notification.Processed && !notification.Deleted)
            {
                context.Notifications.Add(new InboxNotificationReceived(notification));
            }
            if (
                pilot.InStructure
                && notification.ReceiverId == pilot.PilotId
                && notification.SenderId != pilot.PilotId
                && notification.StructureDelivery is { } delivery
                && delivery.StructureId == pilot.DockedLocationId
            )
            {
                (deliveredItems ??= []).UnionWith(delivery.ItemIds);
            }
        }
        if (deliveredItems is { Count: > 0 })
        {
            foreach (var item in inventory.List(pilot.PilotId, pilot.DockedLocationId))
            {
                if (
                    item.OwnerId == pilot.PilotId
                    && item.FlagId == (int)InventoryFlag.StructureDeliveries
                    && deliveredItems.Contains(item.Id)
                )
                {
                    InventoryChangesWire.NotifyItemChange(context, new(item, null), beforeFlight: true);
                }
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
    internal static BlueModel Data(InboxNotification notification) =>
        notification.MilitiaStanding is { } militia ? new InboxMilitiaStandingData(notification.Type, militia)
        : notification.AllianceId is int alliance ? new InboxAllianceData(alliance)
        : notification.KillRight is { } killRight ? new InboxKillRightData(notification.Type, notification.SenderId, killRight.Right)
        : notification.StructureDelivery is { } delivery ? new InboxStructureDeliveryData(notification.SenderId, delivery)
        : new InboxContactData(notification.Content);

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
                ["data"] = Data(Notification),
            }
        );
}

internal sealed record InboxNotificationReceived(InboxNotification Notification) : PilotNotification("OnNotificationReceived")
{
    public override object?[] Arguments =>
        [Notification.Id, (int)Notification.Type, Notification.SenderId, Notification.Created, InboxNotificationReply.Data(Notification)];
}

internal sealed record InboxAllianceData(int AllianceId) : BlueModel
{
    public override object ToBlue() => new Dictionary<string, object?> { ["allianceID"] = AllianceId };
}

internal sealed record InboxStructureDeliveryData(long SenderId, StructureDeliveryContent Content) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["structureID"] = Content.StructureId,
            ["structureTypeID"] = Content.StructureTypeId,
            ["solarsystemID"] = Content.SystemId,
            ["structureShowInfoData"] = new object?[] { "showinfo", Content.StructureTypeId, Content.StructureId },
            ["charID"] = SenderId,
            ["listOfTypesAndQty"] = Content.Types.Select(entry => (object?)new object?[] { entry.Quantity, entry.TypeId }).ToList(),
        };
}

internal sealed record InboxKillRightData(InboxNotificationType Type, long OtherPilotId, KillRight Right) : BlueModel
{
    public override object ToBlue()
    {
        var fields = new Dictionary<string, object?> { ["charID"] = OtherPilotId };
        if (Type is InboxNotificationType.KillRightAvailable or InboxNotificationType.KillRightAvailableOpen)
        {
            fields["price"] = (double)(Right.PriceCents ?? 0) / MarketDefinitions.CentsPerIsk;
        }
        if (
            Right.RestrictedTo is long target
            && Type is InboxNotificationType.KillRightAvailable or InboxNotificationType.KillRightUnavailable
        )
        {
            fields["toEntityID"] = target;
        }
        return fields;
    }
}

internal sealed record KillRightCreated(KillRight Right) : PilotNotification("OnKillRightCreated")
{
    public override object?[] Arguments => [Right.Id, Right.OwnerId, Right.TargetId, Right.ExpiresAt];
}

internal sealed record KillRightUsed(KillRight Right) : PilotNotification("OnKillRightUsed")
{
    public override object?[] Arguments => [Right.Id, Right.TargetId];
}

internal sealed record KillRightListed(long Id) : PilotNotification("OnKillRightForYouSold")
{
    public override object?[] Arguments => [Id];
}

internal sealed record InboxMilitiaStandingData(InboxNotificationType Type, MilitiaStandingNotificationContent Content) : BlueModel
{
    public override object ToBlue()
    {
        var data = new Dictionary<string, object?> { ["factionID"] = (int)Content.Faction };
        if (Type is >= InboxNotificationType.MilitiaMemberStandingWarning and <= InboxNotificationType.MilitiaAllianceCeoStandingRemoval)
        {
            data["characterID"] = Content.CharacterId;
            data["corporationID"] = Content.CorporationId;
            data["requiredStanding"] = Content.RequiredStanding;
            if (Content.WarningDuration is long duration)
            {
                data["characterStanding"] = Content.Standing;
                data["warningDuration"] = duration;
            }
        }
        else if (Content.AllianceId is null)
        {
            data["corpID"] = Content.CorporationId;
        }
        if (Content.AllianceId is int alliance)
        {
            data["allianceID"] = alliance;
        }
        return data;
    }
}
