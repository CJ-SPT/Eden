using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Corporations;

[Injectable(InjectionType.Scoped)]
internal sealed class CorporationContactsMonitor(ICorporationContacts contacts)
{
    private long? _pilot;
    private long _revision;
    private CorporationContactBook? _book;
    public bool IsMonitoring => _book is not null;

    public void Reset()
    {
        _pilot = null;
        _book = null;
        _revision = 0;
    }

    public CorporationContactBook Read(PilotCallContext context)
    {
        long revision = contacts.Revision(context.Pilot.CorpId);
        var book = contacts.Read(context.Pilot.PilotId);
        _pilot = context.Pilot.PilotId;
        _book = book;
        _revision = revision;
        return book;
    }

    public void Watch(PilotCallContext context)
    {
        Advance(context);
        if (_book is null)
        {
            Read(context);
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected || _pilot != context.Pilot.PilotId || _book?.CorporationId != context.Pilot.CorpId)
        {
            Reset();
            return;
        }
        if (_book is not { } before || contacts.Revision(before.CorporationId) == _revision)
        {
            return;
        }
        var after = Read(context);
        if (after.CorporationId != before.CorporationId)
        {
            // Saved membership wins even before the session identity refreshes.
            Reset();
            return;
        }
        var old = before.Contacts.ToDictionary(c => c.ContactId);
        var current = after.Contacts.ToDictionary(c => c.ContactId);
        List<OrganizationContactUpdate> updates = [];
        foreach (long id in old.Keys.Except(current.Keys))
        {
            updates.Add(new(id, null, null));
        }
        foreach (var contact in after.Contacts)
        {
            old.TryGetValue(contact.ContactId, out var previous);
            if (previous is null || previous.Standing != contact.Standing)
            {
                updates.Add(new(contact.ContactId, contact.Standing, null));
            }
            if (previous?.LabelMask != contact.LabelMask && (previous is not null || contact.LabelMask != 0))
            {
                // Native OnOrganizationContactsUpdated applies either standing OR mask
                // to an existing row, so each changed field needs its own tuple.
                updates.Add(new(contact.ContactId, null, contact.LabelMask));
            }
        }
        if (updates.Count > 0)
        {
            context.Notifications.Add(new OrganizationContactsUpdated(after.CorporationId, updates));
        }
    }
}
