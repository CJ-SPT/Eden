using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class ContactsHandler(IPilotContacts contacts) : ISelectedPilotCallHandler
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
            service != "charMgr"
            || method
                is not (
                    "GetContactList"
                    or "AddContact"
                    or "EditContact"
                    or "EditContactsRelationshipID"
                    or "DeleteContacts"
                    or "BlockOwners"
                    or "UnblockOwners"
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
                throw new InvalidDataException("Unexpected personal contact arguments.");
            }
            long pilotId = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetContactList", []):
                    return new PersonalContactsReply(contacts.Read(pilotId));
                case ("GetLabels", []):
                    return new ContactLabelsReply(contacts.Labels(pilotId));
                case ("AddContact", [long agentId]):
                    contacts.AddAgent(pilotId, agentId);
                    break;
                case ("AddContact" or "EditContact", [long id, var standing, bool watchlist, bool notify, var message]):
                    if (message is not null and not string and not byte[])
                    {
                        throw new InvalidDataException("Expected a contact message.");
                    }
                    if (method == "AddContact")
                    {
                        contacts.Add(
                            pilotId,
                            id,
                            Standing(standing),
                            watchlist,
                            notify
                                ? message is null
                                    ? ""
                                    : Text(message)
                                : null
                        );
                    }
                    else
                    {
                        contacts.Edit(
                            pilotId,
                            id,
                            Standing(standing),
                            watchlist,
                            notify
                                ? message is null
                                    ? ""
                                    : Text(message)
                                : null
                        );
                    }
                    break;
                case ("EditContactsRelationshipID", [var ids, var standing]):
                    contacts.EditStanding(pilotId, Ids(ids), Standing(standing));
                    break;
                case ("DeleteContacts", [var ids]):
                    contacts.Delete(pilotId, Ids(ids));
                    break;
                case ("BlockOwners" or "UnblockOwners", [var ids]):
                    contacts.SetBlocked(pilotId, Ids(ids), method == "BlockOwners");
                    break;
                case ("CreateLabel", [var name, var color]):
                    return contacts.CreateLabel(pilotId, Text(name), Color(color));
                case ("EditLabel", [long id, var name, var color]):
                    contacts.EditLabel(pilotId, id, name is null ? null : Text(name), Color(color));
                    break;
                case ("DeleteLabel", [long id]):
                    contacts.DeleteLabel(pilotId, id);
                    break;
                case ("AssignLabels" or "RemoveLabels", [var ids, long mask]):
                    contacts.SetLabels(pilotId, Ids(ids), mask, method == "AssignLabels");
                    break;
                default:
                    return new PilotCallResult.Failure("Invalid personal contact request.");
            }
            // AddressBookSvc applies these changes to its own caches after success.
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
