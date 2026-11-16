using Eden.Gameplay.Calendar;

namespace Eden.Gameplay.Alliances;

public sealed record AllianceCalendarEntry(
    CalendarEvent Event,
    long CreatorId,
    string Description,
    IReadOnlyList<CalendarAttendance> Attendance
);

public interface IAllianceCalendar
{
    bool IsOwner(long ownerId);
    IReadOnlyList<CalendarEvent> Month(long actorId, int month, int year);
    CalendarDetails Details(long actorId, long eventId, long allianceId);
    IReadOnlyList<PersonalCalendarResponse> Responses(long actorId);
    IReadOnlyList<CalendarAttendance> Attendance(long actorId, long eventId, long allianceId);
    long Create(long actorId, CalendarEventDraft draft);
    void Edit(long actorId, long eventId, long oldTime, CalendarEventDraft draft);
    void Respond(long actorId, long eventId, long allianceId, CalendarResponse response);
    void Delete(long actorId, long eventId, long allianceId);
}
