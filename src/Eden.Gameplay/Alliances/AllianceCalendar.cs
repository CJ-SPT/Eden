using Eden.Gameplay.Calendar;

namespace Eden.Gameplay.Alliances;

public sealed record AllianceCalendarEntry(
    CalendarEvent Event,
    long CreatorId,
    string Description,
    IReadOnlyList<CalendarAttendance> Attendance
);

public interface IAllianceCalendar : IOrganizationCalendar;
