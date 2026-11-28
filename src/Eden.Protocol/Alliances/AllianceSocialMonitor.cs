using Eden.Gameplay.Alliances;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Alliances;

[Injectable(InjectionType.Scoped)]
internal sealed class AllianceSocialMonitor(IAllianceSocial social, IAlliances alliances)
{
    private long? _pilot;
    private AllianceSocialBook? _book;

    internal void Reset()
    {
        _pilot = null;
        _book = null;
    }

    internal AllianceSocialBook Read(PilotCallContext context)
    {
        var book = social.Read(context.Pilot.PilotId);
        _pilot = context.Pilot.PilotId;
        _book = book;
        return book;
    }

    internal void Watch(PilotCallContext context)
    {
        Advance(context);
        if (_book is null)
        {
            Read(context);
        }
    }

    internal void Advance(PilotCallContext context)
    {
        if (_book is not { } before)
        {
            return;
        }
        if (!context.Selected || _pilot != context.Pilot.PilotId || alliances.PilotAffiliation(context.Pilot.PilotId) != before.AllianceId)
        {
            Reset();
            return;
        }
        if (social.Revision(before.AllianceId) == before.Revision)
        {
            return;
        }
        AllianceSocialBook after;
        try
        {
            after = Read(context);
        }
        catch (InvalidDataException) when (alliances.PilotAffiliation(context.Pilot.PilotId) != before.AllianceId)
        {
            Reset();
            return;
        }
        if (after.AllianceId != before.AllianceId)
        {
            Reset();
            return;
        }
        var old = before.Contacts.ToDictionary(c => c.ContactId);
        var current = after.Contacts.ToDictionary(c => c.ContactId);
        List<OrganizationContactUpdate> changes = [];
        foreach (long id in old.Keys.Except(current.Keys))
        {
            changes.Add(new(id, null, null));
            context.Notifications.Add(new AllianceRelationshipChanged(after.AllianceId, id, old[id].Standing, null));
        }
        foreach (var contact in after.Contacts)
        {
            var previous = old.GetValueOrDefault(contact.ContactId);
            if (previous is null || previous.Standing != contact.Standing)
            {
                changes.Add(new(contact.ContactId, contact.Standing, null));
                context.Notifications.Add(
                    new AllianceRelationshipChanged(after.AllianceId, contact.ContactId, previous?.Standing, contact.Standing)
                );
            }
            if (previous?.LabelMask != contact.LabelMask && (previous is not null || contact.LabelMask != 0))
            {
                changes.Add(new(contact.ContactId, null, contact.LabelMask));
            }
        }
        if (changes.Count > 0)
        {
            context.Notifications.Add(new OrganizationContactsUpdated(after.AllianceId, changes));
        }
    }
}
