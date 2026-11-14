using Eden.Data.World.Calendar;
using Eden.Data.World.Characters;
using Eden.Gameplay.Calendar;
using Eden.Infrastructure.Registration;
using Eden.World.Characters;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Calendar;

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

    private static void CheckPilot(WorldAction world, long id)
    {
        if (!world.PilotExists(id))
        {
            throw new InvalidDataException("Unknown calendar pilot.");
        }
    }

    private static IEnumerable<SavedCalendarEvent> InviteeEvents(WorldAction world, long pilotId) =>
        world
            .Filter<SavedCalendarParticipant>(p => p.PilotId == pilotId)
            .AsEnumerable()
            .Select(p => world.Set<SavedCalendarEvent>().Find(p.EventId)!)
            .Where(e => e is not null);

    private static SavedCalendarEvent Read(WorldAction world, long pilotId, long eventId, long ownerId) =>
        world.Set<SavedCalendarEvent>().Find(eventId) is { } entry
        && entry.OwnerId == ownerId
        && (ownerId == pilotId || world.Set<SavedCalendarParticipant>().Find(eventId, pilotId) is not null)
            ? entry
            : throw new InvalidDataException("That calendar event is not available to you.");

    private static SavedCalendarEvent Owned(WorldAction world, long pilotId, long eventId) =>
        world.Set<SavedCalendarEvent>().Find(eventId) is { } entry && entry.OwnerId == pilotId
            ? entry
            : throw new InvalidDataException("Only the creator can change this personal event.");

    private void CheckMutable(SavedCalendarEvent entry)
    {
        if (entry.Deleted)
        {
            throw new InvalidDataException("That calendar event has been cancelled.");
        }
        CalendarRules.CheckTime(entry.Time, Now);
    }

    private static void CheckKnown(WorldAction world, long owner, IReadOnlyList<long> ids, bool checkBlocks)
    {
        CalendarRules.CheckInvitees(owner, ids);
        if (ids.Any(id => !world.PilotExists(id)))
        {
            throw new InvalidDataException("Calendar invitations require existing local Eden pilots.");
        }
        if (
            checkBlocks
            && ids.Any(id =>
                world.Set<SavedBlockedContact>().Find(owner, id) is not null || world.Set<SavedBlockedContact>().Find(id, owner) is not null
            )
        )
        {
            throw new InvalidDataException("A blocked pilot cannot receive this invitation.");
        }
    }

    public long Observe(long pilotId)
    {
        return WorldActionScope.Inspect<long>(
            database,
            world =>
            {
                CheckPilot(world, pilotId);
                return world.Filter<SavedCalendarChange>(c => c.PilotId == pilotId).Select(c => (long?)c.Id).Max() ?? 0;
            }
        );
    }

    public CalendarChanges Changes(long pilotId, long after)
    {
        if (after < 0)
        {
            throw new InvalidDataException("Invalid calendar change cursor.");
        }
        return WorldActionScope.Inspect<CalendarChanges>(
            database,
            world =>
            {
                CheckPilot(world, pilotId);
                var items = world
                    .Filter<SavedCalendarChange>(c => c.PilotId == pilotId && c.Id > after)
                    .OrderBy(c => c.Id)
                    .Take(CalendarRules.ChangeBatchSize + 1)
                    .AsEnumerable()
                    .Select(c => c.Snapshot())
                    .ToArray();
                var batch = items.Take(CalendarRules.ChangeBatchSize).ToArray();
                return new CalendarChanges(batch.LastOrDefault()?.Id ?? after, batch, items.Length > batch.Length);
            }
        );
    }

    public IReadOnlyList<CalendarEvent> Month(long pilotId, int month, int year)
    {
        var range = CalendarRules.Month(month, year);
        return WorldActionScope.Inspect<IReadOnlyList<CalendarEvent>>(
            database,
            world =>
            {
                CheckPilot(world, pilotId);
                return world
                    .Filter<SavedCalendarEvent>(e => e.OwnerId == pilotId && e.Time >= range.Start && e.Time < range.End)
                    .AsEnumerable()
                    .Concat(InviteeEvents(world, pilotId).Where(e => e.Time >= range.Start && e.Time < range.End))
                    .OrderBy(e => e.Time)
                    .ThenBy(e => e.Id)
                    .AsEnumerable()
                    .Select(e => e.Snapshot())
                    .ToArray();
            }
        );
    }

    public CalendarDetails Details(long pilotId, long eventId, long ownerId)
    {
        return WorldActionScope.Inspect<CalendarDetails>(
            database,
            world =>
            {
                var entry = Read(world, pilotId, eventId, ownerId);
                return new CalendarDetails(entry.OwnerId, entry.Description);
            }
        );
    }

    public IReadOnlyList<PersonalCalendarResponse> Responses(long pilotId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<PersonalCalendarResponse>>(
            database,
            world =>
            {
                CheckPilot(world, pilotId);
                var owned = world
                    .Filter<SavedCalendarEvent>(e => e.OwnerId == pilotId)
                    .Select(e => new PersonalCalendarResponse(e.Id, e.Deleted ? CalendarResponse.Deleted : CalendarResponse.Accepted))
                    .ToArray();
                var invited = world
                    .Filter<SavedCalendarParticipant>(p => p.PilotId == pilotId)
                    .AsEnumerable()
                    .Select(p => new PersonalCalendarResponse(
                        p.EventId,
                        world.Set<SavedCalendarEvent>().Find(p.EventId)!.Deleted ? CalendarResponse.Deleted : p.Response
                    ));
                return owned.Concat(invited).OrderBy(r => r.EventId).ToArray();
            }
        );
    }

    public IReadOnlyList<CalendarAttendance> Attendance(long pilotId, long eventId, long ownerId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<CalendarAttendance>>(
            database,
            world =>
            {
                _ = Read(world, pilotId, eventId, ownerId);
                return world
                    .Filter<SavedCalendarParticipant>(p => p.EventId == eventId)
                    .OrderBy(p => p.PilotId)
                    .Select(p => new CalendarAttendance(p.PilotId, p.Response))
                    .ToArray();
            }
        );
    }

    private static void AddChange(WorldAction world, SavedCalendarEvent entry, long pilot, CalendarChangeKind kind, long oldTime) =>
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

    private static IReadOnlyList<long> Recipients(WorldAction world, SavedCalendarEvent entry) =>
        world.Filter<SavedCalendarParticipant>(p => p.EventId == entry.Id).Select(p => p.PilotId).ToArray().Append(entry.OwnerId).ToArray();

    private void Commit(WorldActionScope transaction)
    {
        transaction.World.AfterCommit(signal.Publish);
        transaction.Commit();
    }

    public long Create(long pilotId, CalendarEventDraft draft, IReadOnlyList<long> invitees)
    {
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
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
        Commit(transaction);
        return entry.Id;
    }

    public void Edit(long pilotId, long eventId, long oldTime, CalendarEventDraft draft)
    {
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
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
            foreach (var participant in world.Filter<SavedCalendarParticipant>(p => p.EventId == eventId))
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
        Commit(transaction);
    }

    public void UpdateParticipants(long pilotId, long eventId, IReadOnlyList<long> add, IReadOnlyList<long> remove)
    {
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        var entry = Owned(world, pilotId, eventId);
        CheckMutable(entry);
        CheckKnown(world, pilotId, add, checkBlocks: true);
        CheckKnown(world, pilotId, remove, checkBlocks: false);
        if (add.Intersect(remove).Any())
        {
            throw new InvalidDataException("Do not add and remove the same invitee together.");
        }
        var existing = world.Filter<SavedCalendarParticipant>(p => p.EventId == eventId).ToDictionary(p => p.PilotId);
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
        Commit(transaction);
    }

    public void Respond(long pilotId, long eventId, long ownerId, CalendarResponse response)
    {
        CalendarRules.CheckResponse(response);
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
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
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
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
        Commit(transaction);
    }
}
