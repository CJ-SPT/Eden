using Eden.Gameplay.Alliances;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Alliances;

[Injectable]
internal sealed class AllianceSocialHandler(IAllianceSocial social, AllianceSocialMonitor monitor) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "allianceRegistry"
            || method
                is not (
                    "GetAllianceContacts"
                    or "GetContactList"
                    or "AddAllianceContact"
                    or "EditAllianceContact"
                    or "RemoveAllianceContacts"
                    or "EditContactsRelationshipID"
                    or "GetLabels"
                    or "CreateLabel"
                    or "EditLabel"
                    or "DeleteLabel"
                    or "AssignLabels"
                    or "RemoveLabels"
                    or "GetBulletins"
                    or "GetBulletinEntries"
                    or "GetBulletin"
                    or "AddBulletin"
                    or "UpdateBulletin"
                    or "DeleteBulletin"
                    or "UpdateBulletinOrder"
                    or "GetRelationships"
                    or "SetRelationship"
                    or "DeleteRelationship"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (
                keywords is { Count: > 0 }
                && (
                    method is not ("AddBulletin" or "UpdateBulletin")
                    || keywords.Keys.Any(key => key is not ("bulletinID" or "editDateTime"))
                )
            )
            {
                throw new InvalidDataException("Unexpected alliance social arguments.");
            }
            long actor = context.Pilot.PilotId;
            monitor.Watch(context);
            switch (method, args)
            {
                case ("GetAllianceContacts" or "GetContactList", []):
                    return new BlueMap<long, AllianceContactReply>(
                        monitor.Read(context).Contacts.ToDictionary(c => c.ContactId, c => new AllianceContactReply(c))
                    );
                case ("GetRelationships", []):
                    return new AllianceRelationshipsReply(monitor.Read(context));
                case ("GetLabels", []):
                    return new ContactLabelsReply(monitor.Read(context).Labels);
                case ("AddAllianceContact" or "EditAllianceContact", [long id, var value]):
                    social.SetStanding(actor, [id], Standing(value));
                    break;
                case ("SetRelationship", [var value, long id]):
                    social.SetStanding(actor, [id], Standing(value));
                    break;
                case ("EditContactsRelationshipID", [var ids, var value]):
                    social.SetStanding(actor, Ids(ids), Standing(value));
                    break;
                case ("RemoveAllianceContacts", [var ids]):
                    social.DeleteContacts(actor, Ids(ids));
                    break;
                case ("DeleteRelationship", [long id]):
                    social.DeleteContacts(actor, [id]);
                    break;
                case ("CreateLabel", [var name, var color]):
                    long createdLabel = social.CreateLabel(actor, AllianceHandler.Text(name), Color(color));
                    monitor.Advance(context);
                    return createdLabel;
                case ("EditLabel", [long label, var name, var color]):
                    social.EditLabel(actor, label, name is null ? null : AllianceHandler.Text(name), Color(color));
                    break;
                case ("DeleteLabel", [long label]):
                    social.DeleteLabel(actor, label);
                    break;
                case ("AssignLabels" or "RemoveLabels", [var ids, long mask]):
                    social.SetLabels(actor, Ids(ids), mask, method == "AssignLabels");
                    break;
                case ("GetBulletins" or "GetBulletinEntries", [] or [long]):
                    return new AllianceBulletinsReply(social.Read(actor));
                case ("GetBulletin", [long id]):
                    var book = social.Read(actor);
                    return book.Bulletins.SingleOrDefault(b => b.Id == id) is { } bulletin
                        ? new AllianceBulletinReply(book.AllianceId, bulletin)
                        : PilotCallResult.None;
                case ("AddBulletin", [var title, var body, ..]) when args.Length is 2 or 4:
                    return social.SaveBulletin(
                        actor,
                        AllianceHandler.Text(title),
                        AllianceHandler.Text(body),
                        args.Length == 4 ? Optional(args[2]) : Optional(keywords?.GetValueOrDefault("bulletinID")),
                        args.Length == 4 ? Optional(args[3]) : Optional(keywords?.GetValueOrDefault("editDateTime"))
                    );
                case ("UpdateBulletin", [long id, var title, var body, var edited]):
                    return social.SaveBulletin(actor, AllianceHandler.Text(title), AllianceHandler.Text(body), id, Optional(edited));
                case ("DeleteBulletin", [long id]):
                    social.DeleteBulletin(actor, id);
                    break;
                case ("UpdateBulletinOrder", [List<object?> ids]) when ids.All(id => id is long):
                    social.OrderBulletins(actor, ids.Cast<long>().ToArray());
                    break;
                default:
                    throw new InvalidDataException("Invalid alliance social request.");
            }
            monitor.Advance(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static double Standing(object? value) =>
        value switch
        {
            long number => number,
            double number => number,
            _ => throw new InvalidDataException("Expected an alliance standing."),
        };

    private static int? Color(object? value) =>
        value switch
        {
            null => null,
            long color when color is >= 0 and <= int.MaxValue => (int)color,
            _ => throw new InvalidDataException("Choose a label color."),
        };

    private static long? Optional(object? value) =>
        value switch
        {
            null => null,
            long id when id > 0 => id,
            _ => throw new InvalidDataException("Expected a bulletin identifier or revision."),
        };

    private static IReadOnlyList<long> Ids(object? value) =>
        value is List<object?> list && list.Count is > 0 and <= ContactRules.MaximumBatchSize && list.All(id => id is long and > 0)
            ? list.Cast<long>().ToArray()
            : throw new InvalidDataException("Choose a list of alliance contact IDs.");
}

internal sealed record AllianceContactReply(AllianceContact Contact) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["contactID"] = Contact.ContactId,
                ["relationshipID"] = Contact.Standing,
                ["inWatchlist"] = false,
                ["labelMask"] = Contact.LabelMask,
            }
        );
}

internal sealed record AllianceBulletinReply(int AllianceId, AllianceBulletin Bulletin) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                BlueRows.Descriptor([
                    ("bulletinID", BlueColumnType.Int64),
                    ("ownerID", BlueColumnType.Int32),
                    ("title", BlueColumnType.Unicode),
                    ("body", BlueColumnType.Unicode),
                    ("editCharacterID", BlueColumnType.Int64),
                    ("editDateTime", BlueColumnType.Int64),
                    ("sortOrder", BlueColumnType.Int32),
                ]),
                new object?[]
                {
                    Bulletin.Id,
                    AllianceId,
                    new BlueUnicode(Bulletin.Title),
                    new BlueUnicode(Bulletin.Body),
                    Bulletin.EditorId,
                    Bulletin.EditedAt,
                    Bulletin.Order,
                },
            ]
        );
}

internal sealed record AllianceBulletinsReply(AllianceSocialBook Book) : BlueModel
{
    public override object ToBlue() => Book.Bulletins.Select(b => (object?)new AllianceBulletinReply(Book.AllianceId, b)).ToList();
}

internal sealed record AllianceRelationshipsReply(AllianceSocialBook Book) : BlueModel
{
    public override object ToBlue() =>
        new BlueIndexedRowset(
            AllianceRelationshipRow.Header(),
            "toID",
            Book.Contacts.ToDictionary(c => c.ContactId, c => (object?)new AllianceRelationshipRow(c.ContactId, c.Standing))
        );
}

internal sealed record AllianceRelationshipRow(long TargetId, double Standing) : BlueModel
{
    internal static BlueConstructor Header() =>
        BlueRows.Descriptor([("toID", BlueColumnType.Int64), ("relationship", BlueColumnType.Float64)]);

    public override object ToBlue() => new BlueConstructor("blue.DBRow", [Header(), new object?[] { TargetId, Standing }]);
}

internal sealed record AllianceRelationshipChanged(int AllianceId, long TargetId, double? Before, double? After)
    : PilotNotification("OnAllianceRelationshipChanged")
{
    public override object?[] Arguments =>
        [
            AllianceId,
            TargetId,
            AllianceNotificationFields.Delta(
                Before is double old ? (BlueConstructor)new AllianceRelationshipRow(TargetId, old).ToBlue() : null,
                After is double current ? (BlueConstructor)new AllianceRelationshipRow(TargetId, current).ToBlue() : null
            ),
        ];
}
