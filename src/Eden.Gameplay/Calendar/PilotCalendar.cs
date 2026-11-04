namespace Eden.Gameplay.Calendar;

public enum CalendarResponse
{
    Uninvited = 0,
    Deleted = 1,
    Declined = 2,
    Undecided = 3,
    Accepted = 4,
    Maybe = 5,
}

public sealed record CalendarEventDraft(long Time, int? DurationMinutes, string Title, string Description, bool Important);

public sealed record CalendarEvent(
    long Id,
    long OwnerId,
    long Time,
    int? DurationMinutes,
    string Title,
    bool Important,
    bool Deleted,
    long Modified
);

public sealed record CalendarDetails(long CreatorId, string Text);

public sealed record CalendarAttendance(long PilotId, CalendarResponse Response);

public sealed record PersonalCalendarResponse(long EventId, CalendarResponse Response);

public enum CalendarChangeKind
{
    Created,
    Edited,
    Cancelled,
    Uninvited,
}

public sealed record CalendarChange(long Id, CalendarChangeKind Kind, CalendarEvent Event, long OldTime);

public sealed record CalendarChanges(long Cursor, IReadOnlyList<CalendarChange> Items, bool More);

public interface IPilotCalendar
{
    long Revision { get; }
    long Observe(long pilotId);
    CalendarChanges Changes(long pilotId, long after);
    IReadOnlyList<CalendarEvent> Month(long pilotId, int month, int year);
    CalendarDetails Details(long pilotId, long eventId, long ownerId);
    IReadOnlyList<PersonalCalendarResponse> Responses(long pilotId);
    IReadOnlyList<CalendarAttendance> Attendance(long pilotId, long eventId, long ownerId);
    long Create(long pilotId, CalendarEventDraft draft, IReadOnlyList<long> invitees);
    void Edit(long pilotId, long eventId, long oldTime, CalendarEventDraft draft);
    void UpdateParticipants(long pilotId, long eventId, IReadOnlyList<long> add, IReadOnlyList<long> remove);
    void Respond(long pilotId, long eventId, long ownerId, CalendarResponse response);
    void Delete(long pilotId, long eventId, long ownerId);
}

public static class CalendarRules
{
    // Pinned appConst and the native duration selector in eveCalendar.pyj.
    public const int StartYear = 2003;
    public const int MonthsPerYear = 12;
    public const int ViewRangeMonths = MonthsPerYear;
    public const int MaximumTitleLength = 40;
    public const int MaximumDescriptionLength = 500;
    public const int MaximumInvitees = 50;
    public const int MinutesPerHour = 60;
    public const int MaximumDurationMinutes = 24 * MinutesPerHour;
    public const int ChangeBatchSize = 256;

    public static (long Start, long End) Month(int month, int year)
    {
        if (month is < 1 or > 12 || year is < StartYear or >= 9999)
        {
            throw new InvalidDataException("Choose a valid calendar month and year.");
        }
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        return (start.ToFileTimeUtc(), start.AddMonths(1).ToFileTimeUtc());
    }

    public static void CheckTime(long time, long now)
    {
        DateTime start;
        try
        {
            start = DateTime.FromFileTimeUtc(time);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("Choose a valid event time.");
        }
        // The client chooses minute precision, so the current minute is valid.
        if (time < now - now % TimeSpan.TicksPerMinute)
        {
            throw new InvalidDataException("Calendar events must start in the current minute or later.");
        }
        var current = DateTime.FromFileTimeUtc(now);
        int months = (start.Year - current.Year) * MonthsPerYear + start.Month - current.Month;
        if (start.Year < StartYear || months > ViewRangeMonths)
        {
            throw new InvalidDataException("Create calendar events no more than twelve calendar months ahead.");
        }
    }

    public static CalendarEventDraft Check(CalendarEventDraft draft, long now)
    {
        CheckTime(draft.Time, now);
        if (
            draft.DurationMinutes is int duration
            && (duration is < MinutesPerHour or > MaximumDurationMinutes || duration % MinutesPerHour != 0)
        )
        {
            throw new InvalidDataException("Choose an unspecified duration or one to twenty-four whole hours.");
        }
        string title = draft.Title.Trim();
        if (title.Length is 0 or > MaximumTitleLength || title.Any(char.IsControl))
        {
            throw new InvalidDataException("Event titles need 1 to 40 characters without control characters.");
        }
        if (draft.Description.Length > MaximumDescriptionLength || draft.Description.Contains('\0'))
        {
            throw new InvalidDataException("Event descriptions may contain at most 500 characters and no null characters.");
        }
        return draft with { Title = title };
    }

    public static void CheckInvitees(long owner, IReadOnlyList<long> invitees)
    {
        if (invitees.Count > MaximumInvitees || invitees.Any(id => id <= 0 || id == owner) || invitees.Distinct().Count() != invitees.Count)
        {
            throw new InvalidDataException("Invite up to fifty distinct other pilots.");
        }
    }

    public static void CheckResponse(CalendarResponse response)
    {
        if (response is not (CalendarResponse.Accepted or CalendarResponse.Declined or CalendarResponse.Maybe))
        {
            throw new InvalidDataException("Accept, decline or tentatively accept an invitation.");
        }
    }

    public static CalendarResponse AfterReschedule(CalendarResponse response) =>
        response is CalendarResponse.Accepted or CalendarResponse.Maybe ? CalendarResponse.Undecided : response;
}
