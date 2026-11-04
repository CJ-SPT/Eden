using Eden.Gameplay.Calendar;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Calendar;

[Injectable(InjectionType.Scoped)]
internal sealed class CalendarHandler(IPilotCalendar calendar) : ISelectedPilotCallHandler
{
    private long? _pilot;
    private long _cursor;
    private long _revision;
    private bool _more;
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_pilot == context.Pilot.PilotId)
        {
            return;
        }
        _revision = calendar.Revision;
        _cursor = calendar.Observe(context.Pilot.PilotId);
        _pilot = context.Pilot.PilotId;
        _more = false;
    }

    public void Reset()
    {
        _pilot = null;
        _more = false;
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        long revision = calendar.Revision;
        if (!_more && _revision == revision)
        {
            return;
        }
        var changes = calendar.Changes(context.Pilot.PilotId, _cursor);
        _cursor = changes.Cursor;
        _more = changes.More;
        _revision = revision;
        foreach (var change in changes.Items)
        {
            context.Notifications.Add(
                change.Kind switch
                {
                    CalendarChangeKind.Created => new CalendarEventCreated(change.Event),
                    CalendarChangeKind.Edited => new CalendarEventEdited(change.Event, change.OldTime),
                    CalendarChangeKind.Cancelled => new CalendarEventRemoved(change.Event.Id, change.Event.Time, true),
                    CalendarChangeKind.Uninvited => new CalendarEventRemoved(change.Event.Id, change.Event.Time, false),
                    _ => throw new InvalidDataException("Unknown calendar change."),
                }
            );
        }
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
            (service, method)
            is not (

                ("calendarProxy", "GetEventList" or "GetEventDetails")
                or
                (
                    "calendarMgr",
                    "GetResponsesForCharacter"
                        or "GetResponsesToEvent"
                        or "CreatePersonalEvent"
                        or "EditPersonalEvent"
                        or "UpdateEventParticipants"
                        or "SendEventResponse"
                        or "DeleteEvent"
                        or "CreateCorporationEvent"
                        or "EditCorporationEvent"
                        or "CreateAllianceEvent"
                        or "EditAllianceEvent"
                )
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
                && (
                    method is not ("CreatePersonalEvent" or "UpdateEventParticipants")
                    || keywords.Count != 1
                    || !keywords.TryGetValue("approvedCost", out var approved)
                    || approved is not (0L or 0d)
                )
            )
            {
                throw new InvalidDataException("Unexpected calendar arguments. Personal invitations have no configured CSPA fee.");
            }
            long pilot = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetEventList", [long month, long year])
                    when month is >= 1 and <= 12 && year is >= CalendarRules.StartYear and < 9999:
                    return new CalendarMonthReply(calendar.Month(pilot, (int)month, (int)year));
                case ("GetEventDetails", [long id, long owner]):
                    return new CalendarDetailsReply(calendar.Details(pilot, id, owner));
                case ("GetResponsesForCharacter", []):
                    return new BlueList<CalendarPersonalResponseRow>(
                        calendar.Responses(pilot).Select(r => new CalendarPersonalResponseRow(r)).ToArray()
                    );
                case ("GetResponsesToEvent", [long id, long owner]):
                    return new BlueList<CalendarAttendanceRow>(
                        calendar.Attendance(pilot, id, owner).Select(r => new CalendarAttendanceRow(r)).ToArray()
                    );
                case ("CreatePersonalEvent", [long time, var duration, var title, var description, var important, var invitees]):
                    return calendar.Create(pilot, Draft(time, duration, title, description, important), Pilots(invitees));
                case ("EditPersonalEvent", [long id, long old, long time, var duration, var title, var description, var important]):
                    calendar.Edit(pilot, id, old, Draft(time, duration, title, description, important));
                    return PilotCallResult.None;
                case ("UpdateEventParticipants", [long id, var add, var remove]):
                    calendar.UpdateParticipants(pilot, id, Pilots(add), Pilots(remove));
                    return PilotCallResult.None;
                case ("SendEventResponse", [long id, long owner, long response]) when response is >= 0 and <= (long)CalendarResponse.Maybe:
                    calendar.Respond(pilot, id, owner, (CalendarResponse)response);
                    return PilotCallResult.None;
                case ("DeleteEvent", [long id, long owner]):
                    calendar.Delete(pilot, id, owner);
                    return PilotCallResult.None;
                case ("CreateCorporationEvent" or "EditCorporationEvent" or "CreateAllianceEvent" or "EditAllianceEvent", _):
                    throw new InvalidDataException("Corporation and alliance calendar management is not available yet.");
                default:
                    throw new InvalidDataException("Invalid calendar request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static IReadOnlyList<long> Pilots(object? value) =>
        value switch
        {
            null => [],
            List<object?> list when list.Count <= CalendarRules.MaximumInvitees && list.All(item => item is long) => list.Cast<long>()
                .ToArray(),
            _ => throw new InvalidDataException("Expected a list of at most fifty invited pilots."),
        };

    private static CalendarEventDraft Draft(long time, object? duration, object? title, object? description, object? important) =>
        new(
            time,
            duration switch
            {
                null => null,
                long minutes when minutes is >= 0 and <= int.MaxValue => (int)minutes,
                _ => throw new InvalidDataException("Choose a valid event duration."),
            },
            Text(title),
            Text(description),
            important switch
            {
                bool flag => flag,
                0L => false,
                1L => true,
                _ => throw new InvalidDataException("Choose a valid event importance flag."),
            }
        );

    private static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected calendar text.");
}

internal sealed record CalendarMonthReply(IReadOnlyList<CalendarEvent> Events) : BlueModel
{
    // GetEventList returns a list of result groups, not a flat list of events.
    public override object ToBlue() =>
        new List<object?> { new BlueList<CalendarEventRow>(Events.Select(e => new CalendarEventRow(e)).ToArray()) };
}

internal sealed record CalendarEventRow(CalendarEvent Event) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                BlueRows.Descriptor([
                    ("eventID", BlueColumnType.Int64),
                    ("ownerID", BlueColumnType.Int64),
                    ("eventDateTime", BlueColumnType.Int64),
                    ("eventDuration", BlueColumnType.Int32),
                    ("eventTitle", BlueColumnType.Unicode),
                    ("importance", BlueColumnType.Int32),
                    ("isDeleted", BlueColumnType.Boolean),
                    ("dateModified", BlueColumnType.Int64),
                ]),
                new object?[]
                {
                    Event.Id,
                    Event.OwnerId,
                    Event.Time,
                    Event.DurationMinutes,
                    new BlueUnicode(Event.Title),
                    Event.Important ? 1 : 0,
                    Event.Deleted,
                    Event.Modified,
                },
            ]
        );
}

internal sealed record CalendarDetailsReply(CalendarDetails Details) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?> { ["creatorID"] = Details.CreatorId, ["eventText"] = new BlueUnicode(Details.Text) }
        );
}

internal sealed record CalendarPersonalResponseRow(PersonalCalendarResponse Response) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                BlueRows.Descriptor([("eventID", BlueColumnType.Int64), ("status", BlueColumnType.Int32)]),
                new object?[] { Response.EventId, (int)Response.Response },
            ]
        );
}

internal sealed record CalendarAttendanceRow(CalendarAttendance Attendance) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                BlueRows.Descriptor([("characterID", BlueColumnType.Int64), ("status", BlueColumnType.Int32)]),
                new object?[] { Attendance.PilotId, (int)Attendance.Response },
            ]
        );
}

internal sealed record CalendarEventCreated(CalendarEvent Event) : PilotNotification("OnNewCalendarEvent")
{
    public override object?[] Arguments =>
        [Event.Id, Event.OwnerId, Event.Time, Event.DurationMinutes, new BlueUnicode(Event.Title), Event.Important ? 1 : 0];
}

internal sealed record CalendarEventEdited(CalendarEvent Event, long OldTime) : PilotNotification("OnEditCalendarEvent")
{
    public override object?[] Arguments =>
        [Event.Id, Event.OwnerId, OldTime, Event.Time, Event.DurationMinutes, new BlueUnicode(Event.Title), Event.Important ? 1 : 0];
}

internal sealed record CalendarEventRemoved(long Id, long Time, bool Deleted) : PilotNotification("OnRemoveCalendarEvent")
{
    public override object?[] Arguments => [Id, Time, Deleted];
}
