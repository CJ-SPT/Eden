using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationCommunicationHandler(ICorporationCommunications communications) : ISelectedPilotCallHandler
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
            service != "corpRegistry"
            || method
                is not (
                    "GetBulletins"
                    or "AddBulletin"
                    or "DeleteBulletin"
                    or "UpdateBulletinOrder"
                    or "GetCorpWelcomeMail"
                    or "SetCorpWelcomeMail"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            long actor = context.Pilot.PilotId;
            if (method != "AddBulletin" && keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation communication arguments.");
            }
            switch (method, args)
            {
                case ("GetBulletins", []):
                    return new CorporationBulletinsReply(context.Pilot.CorpId, communications.Read(actor).Bulletins);
                case ("AddBulletin", [var title, var body]):
                    if (keywords?.Keys.Any(key => key is not ("bulletinID" or "editDateTime")) == true)
                    {
                        throw new InvalidDataException("Unexpected bulletin arguments.");
                    }
                    return communications.SaveBulletin(
                        actor,
                        BlueMarshal.Text(title),
                        BlueMarshal.Text(body),
                        Optional("bulletinID"),
                        Optional("editDateTime")
                    );
                case ("DeleteBulletin", [long id]):
                    communications.DeleteBulletin(actor, id);
                    break;
                case ("UpdateBulletinOrder", [List<object?> ids]) when ids.All(id => id is long):
                    communications.OrderBulletins(actor, ids.Cast<long>().ToArray());
                    break;
                case ("GetCorpWelcomeMail", []):
                    return new CorporationWelcomeMailReply(communications.Read(actor).WelcomeMail);
                case ("SetCorpWelcomeMail", [var body]):
                    communications.SetWelcomeMail(actor, BlueMarshal.Text(body));
                    break;
                default:
                    throw new InvalidDataException("Invalid corporation communication request.");
            }
            return PilotCallResult.None;

            long? Optional(string key) =>
                keywords?.GetValueOrDefault(key) switch
                {
                    null => null,
                    long value when value > 0 => value,
                    _ => throw new InvalidDataException("Invalid bulletin identifier or revision."),
                };
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CorporationBulletinsReply(int CorporationId, IReadOnlyList<CorporationBulletin> Bulletins) : BlueModel
{
    public override object ToBlue() =>
        Bulletins
            .Select(row =>
                (object?)
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
                            new object?[] { row.Id, CorporationId, row.Title, row.Body, row.EditorId, row.EditedAt, row.Order },
                        ]
                    )
            )
            .ToList();
}

internal sealed record CorporationWelcomeMailReply(CorporationWelcomeMail Mail) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["welcomeMail"] = new BlueUnicode(Mail.Body),
                ["characterID"] = Mail.EditorId,
                ["changeDate"] = Mail.EditedAt,
            }
        );
}

internal sealed record CorporationWelcomeMailChanged(CorporationWelcomeMail Mail) : PilotNotification("OnCorporationWelcomeMailChanged")
{
    public override object?[] Arguments => [Mail.EditorId, Mail.EditedAt];
}
