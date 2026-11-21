using System.Text.Json;
using Eden.Gameplay.Calendar;

namespace Eden.Data.World.Calendar;

public sealed record CorporationCalendarEntry(
    CalendarEvent Event,
    long CreatorId,
    string Description,
    IReadOnlyList<CalendarAttendance> Attendance
);

public sealed class SavedCorporationCalendarEvent
{
    public long Id { get; set; }
    public int CorporationId { get; set; }
    public string DataJson { get; set; } = "";

    public CorporationCalendarEntry Read() => JsonSerializer.Deserialize<CorporationCalendarEntry>(DataJson)!;

    public void Write(CorporationCalendarEntry entry) => DataJson = JsonSerializer.Serialize(entry);
}
