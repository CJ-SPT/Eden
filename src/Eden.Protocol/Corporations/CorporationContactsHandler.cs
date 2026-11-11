using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationContactsHandler(ICorporationContacts contacts, CorporationContactsMonitor monitor)
    : ISelectedPilotCallHandler
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
                    "GetCorporateContacts"
                    or "GetContactList"
                    or "AddCorporateContact"
                    or "EditCorporateContact"
                    or "RemoveCorporateContacts"
                    or "EditContactsRelationshipID"
                    or "GetLabels"
                    or "CreateLabel"
                    or "EditLabel"
                    or "DeleteLabel"
                    or "AssignLabels"
                    or "RemoveLabels"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation contact arguments.");
            }
            long actor = context.Pilot.PilotId;
            if (method is not ("GetCorporateContacts" or "GetContactList" or "GetLabels"))
            {
                monitor.Watch(context);
            }
            switch (method, args)
            {
                case ("GetCorporateContacts" or "GetContactList", []):
                    return new BlueMap<long, CorporationContactReply>(
                        monitor.Read(context).Contacts.ToDictionary(c => c.ContactId, c => new CorporationContactReply(c))
                    );
                case ("GetLabels", []):
                    return new ContactLabelsReply(monitor.Read(context).Labels);
                case ("AddCorporateContact" or "EditCorporateContact", [long id, var standing]):
                    contacts.SetStanding(actor, [id], Standing(standing));
                    break;
                case ("EditContactsRelationshipID", [var ids, var standing]):
                    contacts.SetStanding(actor, Ids(ids), Standing(standing));
                    break;
                case ("RemoveCorporateContacts", [var ids]):
                    contacts.Delete(actor, Ids(ids));
                    break;
                case ("CreateLabel", [var name, var color]):
                    long createdLabel = contacts.CreateLabel(actor, Text(name), Color(color));
                    monitor.Advance(context);
                    return createdLabel;
                case ("EditLabel", [long label, var name, var color]):
                    contacts.EditLabel(actor, label, name is null ? null : Text(name), Color(color));
                    break;
                case ("DeleteLabel", [long label]):
                    contacts.DeleteLabel(actor, label);
                    break;
                case ("AssignLabels" or "RemoveLabels", [var ids, long mask]):
                    contacts.SetLabels(actor, Ids(ids), mask, method == "AssignLabels");
                    break;
                default:
                    throw new InvalidDataException("Invalid corporation contact request.");
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
            _ => throw new InvalidDataException("Expected a contact standing."),
        };

    private static int? Color(object? value) =>
        value switch
        {
            null => null,
            long color when color is >= 0 and <= int.MaxValue => (int)color,
            _ => throw new InvalidDataException("Expected a contact label color."),
        };

    private static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected a contact label name.");

    private static IReadOnlyList<long> Ids(object? value)
    {
        if (value is not List<object?> list || list.Count is < 1 or > ContactRules.MaximumBatchSize || list.Any(id => id is not long))
        {
            throw new InvalidDataException("Expected a list of contact IDs.");
        }
        return list.Cast<long>().ToArray();
    }
}

internal sealed record CorporationContactReply(CorporationContact Contact) : BlueModel
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

internal sealed record OrganizationContactUpdate(long ContactId, double? Standing, long? LabelMask) : BlueModel
{
    public override object ToBlue() => new object?[] { ContactId, Standing, LabelMask };
}

internal sealed record OrganizationContactsUpdated(int OwnerId, IReadOnlyList<OrganizationContactUpdate> Updates)
    : PilotNotification("OnOrganizationContactsUpdated")
{
    public override object?[] Arguments => [OwnerId, new BlueList<OrganizationContactUpdate>(Updates)];
}
