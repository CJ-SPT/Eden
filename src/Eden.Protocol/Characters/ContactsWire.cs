using Eden.Gameplay.Characters;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

internal sealed record PersonalContactReply(PersonalContact Contact) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["contactID"] = Contact.ContactId,
                ["relationshipID"] = Contact.Standing,
                ["inWatchlist"] = Contact.InWatchlist,
                ["labelMask"] = Contact.LabelMask,
            }
        );
}

internal sealed record BlockedContactReply(long ContactId) : BlueModel
{
    public override object ToBlue() => new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["senderID"] = ContactId });
}

internal sealed record PersonalContactsReply(PersonalContacts Contacts) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["addresses"] = new BlueList<PersonalContactReply>(Contacts.Addresses.Select(c => new PersonalContactReply(c)).ToArray()),
                ["blocked"] = new BlueList<BlockedContactReply>(Contacts.Blocked.Select(id => new BlockedContactReply(id)).ToArray()),
            }
        );
}

internal sealed record ContactLabelReply(ContactLabel Label) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["labelID"] = Label.Id,
                ["name"] = new BlueUnicode(Label.Name),
                ["color"] = Label.Color,
            }
        );
}

internal sealed record ContactLabelsReply(IReadOnlyList<ContactLabel> Labels) : BlueModel
{
    public override object ToBlue() => Labels.ToDictionary(l => l.Id, l => (object?)new ContactLabelReply(l));
}
