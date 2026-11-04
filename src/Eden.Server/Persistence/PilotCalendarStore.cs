using Eden.Gameplay.Calendar;
using Eden.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

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

// The durable feed owns delivery order. This process-local signal avoids a new
// SQLite query on every idle pilot tick when no calendar has changed.
[Injectable(InjectionType.Singleton)]
internal sealed class CalendarChangeSignal
{
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);

    public void Publish() => Interlocked.Increment(ref _revision);
}

[Injectable]
internal sealed class PilotCalendarStore(WorldDatabase database, TimeProvider clock, CalendarChangeSignal signal) : IPilotCalendar
{
    public long Revision => signal.Revision;
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static void CheckPilot(WorldDbContext world, long id)
    {
        if (!world.Pilots.Any(p => p.Id == id))
        {
            throw new InvalidDataException("Unknown calendar pilot.");
        }
    }

    private static IQueryable<SavedCalendarEvent> Visible(WorldDbContext world, long pilotId) =>
        world
            .Set<SavedCalendarEvent>()
            .Where(e => e.OwnerId == pilotId || world.Set<SavedCalendarParticipant>().Any(p => p.EventId == e.Id && p.PilotId == pilotId));

    private static SavedCalendarEvent Read(WorldDbContext world, long pilotId, long eventId, long ownerId) =>
        Visible(world, pilotId).SingleOrDefault(e => e.Id == eventId && e.OwnerId == ownerId)
        ?? throw new InvalidDataException("That calendar event is not available to you.");

    private static SavedCalendarEvent Owned(WorldDbContext world, long pilotId, long eventId) =>
        world.Set<SavedCalendarEvent>().SingleOrDefault(e => e.Id == eventId && e.OwnerId == pilotId)
        ?? throw new InvalidDataException("Only the creator can change this personal event.");

    private void CheckMutable(SavedCalendarEvent entry)
    {
        if (entry.Deleted)
        {
            throw new InvalidDataException("That calendar event has been cancelled.");
        }
        CalendarRules.CheckTime(entry.Time, Now);
    }

    private static void CheckKnown(WorldDbContext world, long owner, IReadOnlyList<long> ids, bool checkBlocks)
    {
        CalendarRules.CheckInvitees(owner, ids);
        if (world.Pilots.Count(p => ids.Contains(p.Id)) != ids.Count)
        {
            throw new InvalidDataException("Calendar invitations require existing local Eden pilots.");
        }
        if (
            checkBlocks
            && world
                .Set<SavedBlockedContact>()
                .Any(b => (b.PilotId == owner && ids.Contains(b.ContactId)) || (b.ContactId == owner && ids.Contains(b.PilotId)))
        )
        {
            throw new InvalidDataException("A blocked pilot cannot receive this invitation.");
        }
    }

    public long Observe(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        CheckPilot(world, pilotId);
        return world.Set<SavedCalendarChange>().Where(c => c.PilotId == pilotId).Select(c => (long?)c.Id).Max() ?? 0;
    }

    public CalendarChanges Changes(long pilotId, long after)
    {
        if (after < 0)
        {
            throw new InvalidDataException("Invalid calendar change cursor.");
        }
        using WorldDbContext world = new(database.Path);
        CheckPilot(world, pilotId);
        var items = world
            .Set<SavedCalendarChange>()
            .AsNoTracking()
            .Where(c => c.PilotId == pilotId && c.Id > after)
            .OrderBy(c => c.Id)
            .Take(CalendarRules.ChangeBatchSize + 1)
            .AsEnumerable()
            .Select(c => c.Snapshot())
            .ToArray();
        var batch = items.Take(CalendarRules.ChangeBatchSize).ToArray();
        return new(batch.LastOrDefault()?.Id ?? after, batch, items.Length > batch.Length);
    }

    public IReadOnlyList<CalendarEvent> Month(long pilotId, int month, int year)
    {
        var range = CalendarRules.Month(month, year);
        using WorldDbContext world = new(database.Path);
        CheckPilot(world, pilotId);
        return Visible(world, pilotId)
            .AsNoTracking()
            .Where(e => e.Time >= range.Start && e.Time < range.End)
            .OrderBy(e => e.Time)
            .ThenBy(e => e.Id)
            .AsEnumerable()
            .Select(e => e.Snapshot())
            .ToArray();
    }

    public CalendarDetails Details(long pilotId, long eventId, long ownerId)
    {
        using WorldDbContext world = new(database.Path);
        var entry = Read(world, pilotId, eventId, ownerId);
        return new(entry.OwnerId, entry.Description);
    }

    public IReadOnlyList<PersonalCalendarResponse> Responses(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        CheckPilot(world, pilotId);
        var owned = world
            .Set<SavedCalendarEvent>()
            .Where(e => e.OwnerId == pilotId)
            .Select(e => new PersonalCalendarResponse(e.Id, e.Deleted ? CalendarResponse.Deleted : CalendarResponse.Accepted))
            .ToArray();
        var invited = world
            .Set<SavedCalendarParticipant>()
            .Where(p => p.PilotId == pilotId)
            .Join(
                world.Set<SavedCalendarEvent>(),
                p => p.EventId,
                e => e.Id,
                (p, e) => new PersonalCalendarResponse(e.Id, e.Deleted ? CalendarResponse.Deleted : p.Response)
            )
            .ToArray();
        return owned.Concat(invited).OrderBy(r => r.EventId).ToArray();
    }

    public IReadOnlyList<CalendarAttendance> Attendance(long pilotId, long eventId, long ownerId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        _ = Read(world, pilotId, eventId, ownerId);
        return world
            .Set<SavedCalendarParticipant>()
            .Where(p => p.EventId == eventId)
            .OrderBy(p => p.PilotId)
            .Select(p => new CalendarAttendance(p.PilotId, p.Response))
            .ToArray();
    }

    private static void AddChange(WorldDbContext world, SavedCalendarEvent entry, long pilot, CalendarChangeKind kind, long oldTime) =>
        world.Add(
            new SavedCalendarChange
            {
                PilotId = pilot,
                EventId = entry.Id,
                Kind = kind,
                OwnerId = entry.OwnerId,
                Time = entry.Time,
                OldTime = oldTime,
                DurationMinutes = entry.DurationMinutes,
                Title = entry.Title,
                Important = entry.Important,
                Deleted = entry.Deleted,
                Modified = entry.Modified,
            }
        );

    private static IReadOnlyList<long> Recipients(WorldDbContext world, SavedCalendarEvent entry) =>
        world
            .Set<SavedCalendarParticipant>()
            .Where(p => p.EventId == entry.Id)
            .Select(p => p.PilotId)
            .ToArray()
            .Append(entry.OwnerId)
            .ToArray();

    private void Commit(WorldDbContext world, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        world.SaveChanges();
        transaction.Commit();
        signal.Publish();
    }

    public long Create(long pilotId, CalendarEventDraft draft, IReadOnlyList<long> invitees)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        long now = Now;
        var value = CalendarRules.Check(draft, now);
        CheckPilot(world, pilotId);
        CheckKnown(world, pilotId, invitees, checkBlocks: true);
        var entry = world
            .Add(
                new SavedCalendarEvent
                {
                    OwnerId = pilotId,
                    Time = value.Time,
                    DurationMinutes = value.DurationMinutes,
                    Title = value.Title,
                    Description = value.Description,
                    Important = value.Important,
                    Modified = now,
                }
            )
            .Entity;
        world.SaveChanges();
        foreach (long pilot in invitees)
        {
            world.Add(
                new SavedCalendarParticipant
                {
                    EventId = entry.Id,
                    PilotId = pilot,
                    Response = CalendarResponse.Undecided,
                }
            );
        }
        foreach (long pilot in invitees.Append(pilotId))
        {
            AddChange(world, entry, pilot, CalendarChangeKind.Created, entry.Time);
        }
        Commit(world, transaction);
        return entry.Id;
    }

    public void Edit(long pilotId, long eventId, long oldTime, CalendarEventDraft draft)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var entry = Owned(world, pilotId, eventId);
        CheckMutable(entry);
        var value = CalendarRules.Check(draft, Now);
        if (entry.Time != oldTime)
        {
            throw new InvalidDataException("The event time changed. Reopen the event before editing it.");
        }
        if (
            entry.Time == value.Time
            && entry.DurationMinutes == value.DurationMinutes
            && entry.Title == value.Title
            && entry.Description == value.Description
            && entry.Important == value.Important
        )
        {
            return;
        }
        if (entry.Time != value.Time)
        {
            foreach (var participant in world.Set<SavedCalendarParticipant>().Where(p => p.EventId == eventId))
            {
                participant.Response = CalendarRules.AfterReschedule(participant.Response);
            }
        }
        entry.Time = value.Time;
        entry.DurationMinutes = value.DurationMinutes;
        entry.Title = value.Title;
        entry.Description = value.Description;
        entry.Important = value.Important;
        entry.Modified = Now;
        foreach (long pilot in Recipients(world, entry))
        {
            AddChange(world, entry, pilot, CalendarChangeKind.Edited, oldTime);
        }
        Commit(world, transaction);
    }

    public void UpdateParticipants(long pilotId, long eventId, IReadOnlyList<long> add, IReadOnlyList<long> remove)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var entry = Owned(world, pilotId, eventId);
        CheckMutable(entry);
        CheckKnown(world, pilotId, add, checkBlocks: true);
        CheckKnown(world, pilotId, remove, checkBlocks: false);
        if (add.Intersect(remove).Any())
        {
            throw new InvalidDataException("Do not add and remove the same invitee together.");
        }
        var existing = world.Set<SavedCalendarParticipant>().Where(p => p.EventId == eventId).ToDictionary(p => p.PilotId);
        var added = add.Except(existing.Keys).ToArray();
        var removed = remove.Intersect(existing.Keys).ToArray();
        if (existing.Count + added.Length - removed.Length > CalendarRules.MaximumInvitees)
        {
            throw new InvalidDataException("An event may invite at most fifty other pilots.");
        }
        if (added.Length == 0 && removed.Length == 0)
        {
            return;
        }
        entry.Modified = Now;
        foreach (long pilot in removed)
        {
            world.Remove(existing[pilot]);
            AddChange(world, entry, pilot, CalendarChangeKind.Uninvited, entry.Time);
        }
        foreach (long pilot in added)
        {
            world.Add(
                new SavedCalendarParticipant
                {
                    EventId = eventId,
                    PilotId = pilot,
                    Response = CalendarResponse.Undecided,
                }
            );
            AddChange(world, entry, pilot, CalendarChangeKind.Created, entry.Time);
        }
        foreach (long pilot in existing.Keys.Except(removed).Append(pilotId))
        {
            AddChange(world, entry, pilot, CalendarChangeKind.Edited, entry.Time);
        }
        Commit(world, transaction);
    }

    public void Respond(long pilotId, long eventId, long ownerId, CalendarResponse response)
    {
        CalendarRules.CheckResponse(response);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var entry = Read(world, pilotId, eventId, ownerId);
        CheckMutable(entry);
        var invitation =
            world.Set<SavedCalendarParticipant>().Find(eventId, pilotId)
            ?? throw new InvalidDataException("Only an invited pilot can send this response.");
        invitation.Response = response;
        world.SaveChanges();
        transaction.Commit();
        // The native response call updates its own cache. Attendee queries are
        // uncached Blue replies, so reopening the event reads the saved replies.
    }

    public void Delete(long pilotId, long eventId, long ownerId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var entry = Read(world, pilotId, eventId, ownerId);
        if (entry.OwnerId != pilotId)
        {
            throw new InvalidDataException("Only the creator can cancel this personal event.");
        }
        if (entry.Deleted)
        {
            return;
        }
        CheckMutable(entry);
        entry.Deleted = true;
        entry.Modified = Now;
        foreach (long pilot in Recipients(world, entry))
        {
            AddChange(world, entry, pilot, CalendarChangeKind.Cancelled, entry.Time);
        }
        Commit(world, transaction);
    }
}
