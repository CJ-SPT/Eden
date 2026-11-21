using System.Text.Json;
using Eden.Data.World.Calendar;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Calendar;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Calendar;

[Injectable]
internal sealed class CorporationCalendarStore(WorldDatabase database, TimeProvider clock) : ICorporationCalendar
{
    private LiveWorld Resident => LiveWorld.Find(database.Path) ?? throw new InvalidDataException("The world is not running.");
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public bool IsOwner(long ownerId) =>
        ownerId is > 0 and <= int.MaxValue
        && LiveWorld.Find(database.Path)?.OwnerDirectory.Snapshot.Corporations.ContainsKey((int)ownerId) == true;

    private IReadOnlyList<CorporationCalendarEntry> Entries(long pilotId) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
            {
                int corporationId = world.Pilots.Find(pilotId)?.CorporationId ?? throw new InvalidDataException("Unknown pilot.");
                return world
                    .Filter<SavedCorporationCalendarEvent>(row => row.CorporationId == corporationId)
                    .AsEnumerable()
                    .Select(row => row.Read())
                    .ToArray();
            })
        ?? [];

    public IReadOnlyList<CalendarEvent> Month(long pilotId, int month, int year)
    {
        var range = CalendarRules.Month(month, year);
        return Entries(pilotId)
            .Select(row => row.Event)
            .Where(e => e.Time >= range.Start && e.Time < range.End)
            .OrderBy(e => e.Time)
            .ThenBy(e => e.Id)
            .ToArray();
    }

    public IReadOnlyList<PersonalCalendarResponse> Responses(long pilotId) =>
        Entries(pilotId)
            .Select(row => new PersonalCalendarResponse(
                row.Event.Id,
                row.Event.Deleted
                    ? CalendarResponse.Deleted
                    : row.Attendance.SingleOrDefault(a => a.PilotId == pilotId)?.Response ?? CalendarResponse.Undecided
            ))
            .OrderBy(row => row.EventId)
            .ToArray();

    private static SavedCorporationCalendarEvent Visible(WorldAction world, long pilotId, long eventId, long ownerId) =>
        world.Set<SavedCorporationCalendarEvent>().Find(eventId) is { } row
        && row.CorporationId == ownerId
        && world.Pilots.Find(pilotId)?.CorporationId == row.CorporationId
            ? row
            : throw new InvalidDataException("This corporation calendar event is unavailable.");

    public CalendarDetails Details(long pilotId, long eventId, long ownerId) =>
        Resident.Economy.Inspect(world =>
        {
            var entry = Visible(world, pilotId, eventId, ownerId).Read();
            return new CalendarDetails(entry.CreatorId, entry.Description);
        });

    public IReadOnlyList<CalendarAttendance> Attendance(long pilotId, long eventId, long ownerId) =>
        Resident.Economy.Inspect(world => Visible(world, pilotId, eventId, ownerId).Read().Attendance);

    private static int EditorCorporation(WorldAction world, long pilotId)
    {
        var corporation = CorporationRoleStore.Current(world, pilotId);
        if (corporation.ClosedAt.HasValue || !CorporationRoleStore.Has(world, pilotId, corporation, CorporationRole.ChatManager))
        {
            throw new InvalidDataException("Corporation calendar management requires the Communications Officer role.");
        }
        return corporation.Id;
    }

    private static void Notify(WorldAction world, int corporationId, CalendarChangeKind kind, CalendarEvent entry, long oldTime)
    {
        string change = JsonSerializer.Serialize(new CalendarChange(0, kind, entry, oldTime));
        foreach (long pilotId in world.Filter<Pilot>(p => p.CorporationId == corporationId).Select(p => p.Id))
        {
            world.Add(new SavedCorporationChange { PilotId = pilotId, CalendarJson = change });
        }
    }

    public long Create(long pilotId, CalendarEventDraft draft)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        int corporationId = EditorCorporation(world, pilotId);
        long now = Now;
        var value = CalendarRules.Check(draft, now);
        long id = world.Next("WorldCalendarEvent");
        CalendarEvent entry = new(id, corporationId, value.Time, value.DurationMinutes, value.Title, value.Important, false, now);
        var saved = new SavedCorporationCalendarEvent { Id = id, CorporationId = corporationId };
        saved.Write(new(entry, pilotId, value.Description, [new(pilotId, CalendarResponse.Accepted)]));
        world.Add(saved);
        Notify(world, corporationId, CalendarChangeKind.Created, entry, entry.Time);
        world.Commit();
        return id;
    }

    public void Edit(long pilotId, long eventId, long oldTime, CalendarEventDraft draft)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        int corporationId = EditorCorporation(world, pilotId);
        var row = Visible(world, pilotId, eventId, corporationId);
        var before = row.Read();
        long now = Now;
        var value = CalendarRules.Check(draft, now);
        var entry = CalendarRules.Edit(before.Event, oldTime, value, now);
        if (entry with { Modified = before.Event.Modified } == before.Event && value.Description == before.Description)
        {
            return;
        }
        var attendance =
            oldTime == entry.Time
                ? before.Attendance
                : before.Attendance.Select(a => a with { Response = CalendarRules.AfterReschedule(a.Response) }).ToArray();
        row.Write(before with { Event = entry, Description = value.Description, Attendance = attendance });
        Notify(world, corporationId, CalendarChangeKind.Edited, entry, oldTime);
        world.Commit();
    }

    public void Respond(long pilotId, long eventId, long ownerId, CalendarResponse response)
    {
        CalendarRules.CheckResponse(response);
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var row = Visible(world, pilotId, eventId, ownerId);
        var entry = row.Read();
        CalendarRules.CheckMutable(entry.Event, Now);
        if (entry.Attendance.SingleOrDefault(a => a.PilotId == pilotId)?.Response == response)
        {
            return;
        }
        row.Write(
            entry with
            {
                Attendance = entry
                    .Attendance.Where(a => a.PilotId != pilotId)
                    .Append(new(pilotId, response))
                    .OrderBy(a => a.PilotId)
                    .ToArray(),
            }
        );
        world.Commit();
    }

    public void Delete(long pilotId, long eventId, long ownerId)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        int corporationId = EditorCorporation(world, pilotId);
        var row = Visible(world, pilotId, eventId, ownerId);
        var before = row.Read();
        if (before.Event.Deleted)
        {
            return;
        }
        long now = Now;
        CalendarRules.CheckMutable(before.Event, now);
        var entry = before.Event with { Deleted = true, Modified = Math.Max(now, before.Event.Modified + 1) };
        row.Write(before with { Event = entry });
        Notify(world, corporationId, CalendarChangeKind.Cancelled, entry, entry.Time);
        world.Commit();
    }
}
