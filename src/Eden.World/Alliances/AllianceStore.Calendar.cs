using Eden.Data.World.Alliances;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Calendar;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Alliances;

internal sealed partial class AllianceStore
{
    bool IAllianceCalendar.IsOwner(long ownerId) =>
        ownerId is > 0 and <= int.MaxValue
        && LiveWorld.Find(database.Path)?.Economy.Inspect(world => world.Set<SavedAlliance>().Find((int)ownerId) is not null) == true;

    private AllianceSocialBook? CalendarBook(long actorId)
    {
        if (LiveWorld.Find(database.Path) is null)
        {
            return null;
        }
        using var coherent = Resident.EnterPersistence("resident alliance calendar read", TransactionParticipants.None);
        int? id = Resident.Economy.Inspect(world =>
            world.Pilots.Find(actorId) is { } pilot ? world.Set<SavedAllianceMember>().Find(pilot.CorporationId)?.AllianceId : null
        );
        return id.HasValue ? Social(id.Value) : null;
    }

    private AllianceCalendarEntry CalendarEntry(long actorId, long eventId, long allianceId)
    {
        var book = Read(actorId);
        return book.AllianceId == allianceId && book.Events.SingleOrDefault(e => e.Event.Id == eventId) is { } entry
            ? entry
            : throw new InvalidDataException("This alliance calendar event is unavailable.");
    }

    IReadOnlyList<CalendarEvent> IAllianceCalendar.Month(long actorId, int month, int year)
    {
        var range = CalendarRules.Month(month, year);
        return CalendarBook(actorId)
                ?.Events.Select(e => e.Event)
                .Where(e => e.Time >= range.Start && e.Time < range.End)
                .OrderBy(e => e.Time)
                .ThenBy(e => e.Id)
                .ToArray()
            ?? [];
    }

    CalendarDetails IAllianceCalendar.Details(long actorId, long eventId, long allianceId)
    {
        var entry = CalendarEntry(actorId, eventId, allianceId);
        return new(entry.CreatorId, entry.Description);
    }

    IReadOnlyList<PersonalCalendarResponse> IAllianceCalendar.Responses(long actorId) =>
        CalendarBook(actorId)
            ?.Events.Select(e => new PersonalCalendarResponse(
                e.Event.Id,
                e.Event.Deleted
                    ? CalendarResponse.Deleted
                    : e.Attendance.SingleOrDefault(a => a.PilotId == actorId)?.Response ?? CalendarResponse.Undecided
            ))
            .ToArray()
        ?? [];

    IReadOnlyList<CalendarAttendance> IAllianceCalendar.Attendance(long actorId, long eventId, long allianceId) =>
        CalendarEntry(actorId, eventId, allianceId).Attendance;

    long IAllianceCalendar.Create(long actorId, CalendarEventDraft draft)
    {
        var value = CalendarRules.Check(draft, Now);
        long id = 0;
        ChangeSocial(
            actorId,
            (world, state) =>
            {
                // Share the personal-calendar ID allocator without creating a pilot-owned shadow event.
                id = world.Next("WorldCalendarEvent");
                CalendarEvent entry = new(
                    id,
                    state.AllianceId,
                    value.Time,
                    value.DurationMinutes,
                    value.Title,
                    value.Important,
                    false,
                    Now
                );
                Record(world, new(0, state.AllianceId, 0, Calendar: new(0, CalendarChangeKind.Created, entry, entry.Time)));
                return state with
                {
                    Events = state
                        .Events.Append(new(entry, actorId, value.Description, [new(actorId, CalendarResponse.Accepted)]))
                        .ToArray(),
                };
            }
        );
        return id;
    }

    void IAllianceCalendar.Edit(long actorId, long eventId, long oldTime, CalendarEventDraft draft)
    {
        var value = CalendarRules.Check(draft, Now);
        ChangeSocial(
            actorId,
            (world, state) =>
            {
                var before =
                    state.Events.SingleOrDefault(e => e.Event.Id == eventId)
                    ?? throw new InvalidDataException("Unknown alliance calendar event.");
                CheckCalendarMutable(before);
                if (before.Event.Time != oldTime)
                {
                    throw new InvalidDataException("The alliance event time changed. Reload before editing.");
                }
                var changed = before with
                {
                    Event = before.Event with
                    {
                        Time = value.Time,
                        DurationMinutes = value.DurationMinutes,
                        Title = value.Title,
                        Important = value.Important,
                        Modified = Math.Max(Now, before.Event.Modified + 1),
                    },
                    Description = value.Description,
                    Attendance =
                        before.Event.Time == value.Time
                            ? before.Attendance
                            : before.Attendance.Select(a => a with { Response = CalendarRules.AfterReschedule(a.Response) }).ToArray(),
                };
                Record(world, new(0, state.AllianceId, 0, Calendar: new(0, CalendarChangeKind.Edited, changed.Event, oldTime)));
                return state with { Events = state.Events.Select(e => e.Event.Id == eventId ? changed : e).ToArray() };
            }
        );
    }

    private void CheckCalendarMutable(AllianceCalendarEntry entry)
    {
        if (entry.Event.Deleted)
        {
            throw new InvalidDataException("The alliance event has been cancelled.");
        }
        CalendarRules.CheckTime(entry.Event.Time, Now);
    }

    void IAllianceCalendar.Respond(long actorId, long eventId, long allianceId, CalendarResponse response)
    {
        CalendarRules.CheckResponse(response);
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        int current = MemberAlliance(world, actorId);
        var state = Social(world, current);
        var entry = state.Events.SingleOrDefault(e => e.Event.Id == eventId);
        if (allianceId != current || entry is null)
        {
            throw new InvalidDataException("This alliance event is unavailable.");
        }
        CheckCalendarMutable(entry);
        var replies = entry.Attendance.Where(a => a.PilotId != actorId).Append(new(actorId, response)).OrderBy(a => a.PilotId).ToArray();
        SaveSocial(
            world,
            state,
            state with
            {
                Events = state.Events.Select(e => e.Event.Id == eventId ? e with { Attendance = replies } : e).ToArray(),
            }
        );
        world.Commit();
    }

    void IAllianceCalendar.Delete(long actorId, long eventId, long allianceId) =>
        ChangeSocial(
            actorId,
            (world, state) =>
            {
                var entry = state.Events.SingleOrDefault(e => e.Event.Id == eventId);
                if (state.AllianceId != allianceId || entry is null)
                {
                    throw new InvalidDataException("This alliance event is unavailable.");
                }
                if (entry.Event.Deleted)
                {
                    return state;
                }
                CheckCalendarMutable(entry);
                var changed = entry.Event with { Deleted = true, Modified = Math.Max(Now, entry.Event.Modified + 1) };
                Record(world, new(0, state.AllianceId, 0, Calendar: new(0, CalendarChangeKind.Cancelled, changed, changed.Time)));
                return state with { Events = state.Events.Select(e => e.Event.Id == eventId ? e with { Event = changed } : e).ToArray() };
            }
        );
}
