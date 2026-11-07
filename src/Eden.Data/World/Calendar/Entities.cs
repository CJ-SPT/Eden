using Eden.Gameplay.Calendar;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Calendar;

public sealed class SavedCalendarChange
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long EventId { get; set; }
    public CalendarChangeKind Kind { get; set; }
    public long OwnerId { get; set; }
    public long Time { get; set; }
    public long OldTime { get; set; }
    public int? DurationMinutes { get; set; }
    public string Title { get; set; } = "";
    public bool Important { get; set; }
    public bool Deleted { get; set; }
    public long Modified { get; set; }

    public CalendarChange Snapshot() =>
        new(Id, Kind, new(EventId, OwnerId, Time, DurationMinutes, Title, Important, Deleted, Modified), OldTime);
}

public sealed class SavedCalendarEvent
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public long Time { get; set; }
    public int? DurationMinutes { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Important { get; set; }
    public bool Deleted { get; set; }
    public long Modified { get; set; }

    public CalendarEvent Snapshot() => new(Id, OwnerId, Time, DurationMinutes, Title, Important, Deleted, Modified);
}

public sealed class SavedCalendarParticipant
{
    public long EventId { get; set; }
    public long PilotId { get; set; }
    public CalendarResponse Response { get; set; }
}
