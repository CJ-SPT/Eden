using Eden.Gameplay.Calendar;
using Eden.Protocol.Alliances;
using Eden.Protocol.Calendar;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Corporations;

internal sealed partial class CorporationHandler
{
    private long _allianceCursor;

    private void DrainAllianceChanges(PilotCallContext context)
    {
        long head = allianceMembership.Head;
        if (_observedPilot != context.Pilot.PilotId || head <= _allianceCursor)
        {
            return;
        }
        foreach (var change in allianceMembership.Changes(context.Pilot.PilotId, _allianceCursor))
        {
            // A command can commit between the head read and the coherent feed read.
            // Acknowledge every observed record, including those newer than that head.
            _allianceCursor = Math.Max(_allianceCursor, change.Id);
            if (change.Before is not null || change.After is not null)
            {
                context.Notifications.Add(new AllianceChanged(change));
            }
            if (change.MemberBefore is not null || change.MemberAfter is not null)
            {
                context.Notifications.Add(new AllianceMemberChanged(change));
            }
            if (change.ApplicationBefore is not null || change.ApplicationAfter is not null)
            {
                context.Notifications.Add(new AllianceApplicationChanged(change));
            }
            if (change.Calendar is { } calendar)
            {
                context.Notifications.Add(
                    calendar.Kind switch
                    {
                        CalendarChangeKind.Created => new CalendarEventCreated(calendar.Event),
                        CalendarChangeKind.Edited => new CalendarEventEdited(calendar.Event, calendar.OldTime),
                        CalendarChangeKind.Cancelled => new CalendarEventRemoved(calendar.Event.Id, calendar.Event.Time, true),
                        _ => throw new InvalidDataException("Unknown alliance calendar change."),
                    }
                );
            }
        }
        _allianceCursor = Math.Max(_allianceCursor, head);
    }
}
