using Eden.Data.World.Calendar;
using Eden.Gameplay.Calendar;
using Eden.Gameplay.Characters;
using Eden.Server;
using Eden.World.Calendar;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class CalendarTestData
{
    internal const long Owner = 90000001;
    internal const long Guest = 90000002;
    internal const long Other = 90000100;

    internal static TestClock Clock() => new(new DateTimeOffset(2026, 11, 4, 10, 30, 15, TimeSpan.Zero));

    internal static CalendarEventDraft Draft(TestClock clock) =>
        new(clock.Now + TimeSpan.FromHours(2).Ticks, 60, "Mining fleet", "Bring miners", true);

    internal static IReadOnlyList<long> AddPilots(TestWorld db, int count)
    {
        using WorldDbContext world = new(db.Path);
        using var transaction = world.Database.BeginTransaction();
        world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
        List<long> pilots = [];
        for (int i = 0; i < count; i++)
        {
            long pilot = Other + i;
            long account = 1000100 + i;
            long ship = 3000000000100 + i;
            string name = $"Calendar {i}";
            world.Database.ExecuteSql($"INSERT INTO WorldAccount(Id,UserName) VALUES ({account},{name})");
            world.Database.ExecuteSql(
                $"INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId) VALUES ({pilot},{account},{name},1000044,{ship})"
            );
            world.Database.ExecuteSql(
                $"INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES ({ship},{pilot},601,60003760,'Ibis')"
            );
            pilots.Add(pilot);
        }
        transaction.Commit();
        return pilots;
    }
}

public sealed class CalendarTests
{
    [Fact]
    public void InvitationsRepliesDetailsAndCancellationArePrivateAndSurviveRestart()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        CalendarTestData.AddPilots(db, 1);
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        var draft = CalendarTestData.Draft(clock) with
        {
            Title = "  Étoile 学習  ",
            Description = "<b>会合</b>\nBring ships 🚀",
            DurationMinutes = null,
        };
        long id = calendar.Create(CalendarTestData.Owner, draft, [CalendarTestData.Guest]);
        var owner = Assert.Single(calendar.Month(CalendarTestData.Owner, 11, 2026));
        Assert.Equal(id, owner.Id);
        Assert.Equal("Étoile 学習", owner.Title);
        Assert.Null(owner.DurationMinutes);
        Assert.Equal(owner, Assert.Single(calendar.Month(CalendarTestData.Guest, 11, 2026)));
        Assert.Empty(calendar.Month(CalendarTestData.Other, 11, 2026));
        Assert.Equal(
            new CalendarDetails(CalendarTestData.Owner, draft.Description),
            calendar.Details(CalendarTestData.Guest, id, CalendarTestData.Owner)
        );
        Assert.Throws<InvalidDataException>(() => calendar.Details(CalendarTestData.Other, id, CalendarTestData.Owner));
        Assert.Throws<InvalidDataException>(() => calendar.Details(CalendarTestData.Guest, id, CalendarTestData.Guest));
        Assert.Throws<InvalidDataException>(() => calendar.Attendance(CalendarTestData.Other, id, CalendarTestData.Owner));
        Assert.Equal(CalendarResponse.Accepted, Assert.Single(calendar.Responses(CalendarTestData.Owner)).Response);
        Assert.Equal(CalendarResponse.Undecided, Assert.Single(calendar.Responses(CalendarTestData.Guest)).Response);
        calendar.Respond(CalendarTestData.Guest, id, CalendarTestData.Owner, CalendarResponse.Accepted);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        calendar = reopened.GetRequiredService<IPilotCalendar>();
        Assert.Equal(
            CalendarResponse.Accepted,
            Assert.Single(calendar.Attendance(CalendarTestData.Owner, id, CalendarTestData.Owner)).Response
        );
        Assert.Equal(draft.Description, calendar.Details(CalendarTestData.Owner, id, CalendarTestData.Owner).Text);
        calendar.Delete(CalendarTestData.Owner, id, CalendarTestData.Owner);
        calendar.Delete(CalendarTestData.Owner, id, CalendarTestData.Owner);
        Assert.True(Assert.Single(calendar.Month(CalendarTestData.Guest, 11, 2026)).Deleted);
        Assert.Equal(CalendarResponse.Deleted, Assert.Single(calendar.Responses(CalendarTestData.Guest)).Response);
        Assert.Throws<InvalidDataException>(() =>
            calendar.Respond(CalendarTestData.Guest, id, CalendarTestData.Owner, CalendarResponse.Maybe)
        );
        Assert.Throws<InvalidDataException>(() => calendar.Edit(CalendarTestData.Owner, id, draft.Time, draft));
        Assert.Throws<InvalidDataException>(() => calendar.UpdateParticipants(CalendarTestData.Owner, id, [], [CalendarTestData.Guest]));
        WorldInitialization.Open(db.Path);
        Assert.True(Assert.Single(calendar.Month(CalendarTestData.Owner, 11, 2026)).Deleted);
        Assert.Equal(2, calendar.Changes(CalendarTestData.Guest, 0).Items.Count);
    }

    [Theory]
    [InlineData(CalendarResponse.Accepted, CalendarResponse.Undecided)]
    [InlineData(CalendarResponse.Maybe, CalendarResponse.Undecided)]
    [InlineData(CalendarResponse.Declined, CalendarResponse.Declined)]
    public void ReschedulingResetsOnlyPositiveResponsesAndMovesTheEventAcrossMonths(CalendarResponse response, CalendarResponse expected)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        var draft = CalendarTestData.Draft(clock);
        long id = calendar.Create(CalendarTestData.Owner, draft, [CalendarTestData.Guest]);
        calendar.Respond(CalendarTestData.Guest, id, CalendarTestData.Owner, response);
        calendar.Edit(CalendarTestData.Owner, id, draft.Time, draft with { Description = "Updated text" });
        Assert.Equal(response, Assert.Single(calendar.Responses(CalendarTestData.Guest)).Response);
        long moved = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        calendar.Edit(CalendarTestData.Owner, id, draft.Time, draft with { Time = moved });
        Assert.Equal(expected, Assert.Single(calendar.Responses(CalendarTestData.Guest)).Response);
        Assert.Equal(CalendarResponse.Accepted, Assert.Single(calendar.Responses(CalendarTestData.Owner)).Response);
        Assert.Empty(calendar.Month(CalendarTestData.Guest, 11, 2026));
        Assert.Equal(moved, Assert.Single(calendar.Month(CalendarTestData.Guest, 12, 2026)).Time);
        Assert.Throws<InvalidDataException>(() => calendar.Edit(CalendarTestData.Owner, id, draft.Time, draft));
        var changes = calendar.Changes(CalendarTestData.Guest, 0).Items;
        Assert.Equal(
            new[] { CalendarChangeKind.Created, CalendarChangeKind.Edited, CalendarChangeKind.Edited },
            changes.Select(c => c.Kind)
        );
        Assert.Equal(draft.Time, changes[^1].OldTime);
    }

    [Fact]
    public void ParticipantUpdatesPreserveExistingRepliesRevokeAccessAndAreIdempotent()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        CalendarTestData.AddPilots(db, 1);
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        long id = calendar.Create(CalendarTestData.Owner, CalendarTestData.Draft(clock), [CalendarTestData.Guest]);
        calendar.Respond(CalendarTestData.Guest, id, CalendarTestData.Owner, CalendarResponse.Accepted);
        calendar.UpdateParticipants(CalendarTestData.Owner, id, [CalendarTestData.Guest, CalendarTestData.Other], []);
        Assert.Equal(CalendarResponse.Accepted, calendar.Attendance(CalendarTestData.Owner, id, CalendarTestData.Owner)[0].Response);
        Assert.Equal(CalendarResponse.Undecided, Assert.Single(calendar.Responses(CalendarTestData.Other)).Response);
        long revision = calendar.Revision;
        calendar.UpdateParticipants(CalendarTestData.Owner, id, [CalendarTestData.Other], []);
        Assert.Equal(revision, calendar.Revision);
        calendar.UpdateParticipants(CalendarTestData.Owner, id, [], [CalendarTestData.Guest]);
        Assert.Empty(calendar.Month(CalendarTestData.Guest, 11, 2026));
        Assert.Empty(calendar.Responses(CalendarTestData.Guest));
        Assert.Throws<InvalidDataException>(() => calendar.Details(CalendarTestData.Guest, id, CalendarTestData.Owner));
        Assert.Throws<InvalidDataException>(() =>
            calendar.Respond(CalendarTestData.Guest, id, CalendarTestData.Owner, CalendarResponse.Accepted)
        );
        Assert.Equal(CalendarChangeKind.Uninvited, calendar.Changes(CalendarTestData.Guest, 0).Items[^1].Kind);
        calendar.UpdateParticipants(CalendarTestData.Owner, id, [CalendarTestData.Guest], []);
        Assert.Equal(CalendarResponse.Undecided, Assert.Single(calendar.Responses(CalendarTestData.Guest)).Response);
    }

    [Fact]
    public void UnknownBlockedSelfDuplicateAndOversizedInviteBatchesAreRejectedWithoutPartialChanges()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var extras = CalendarTestData.AddPilots(db, CalendarRules.MaximumInvitees + 1);
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        var draft = CalendarTestData.Draft(clock);
        foreach (
            long[] ids in new long[][]
            {
                [CalendarTestData.Owner],
                [CalendarTestData.Guest, CalendarTestData.Guest],
                [CalendarTestData.Guest, long.MaxValue],
                extras.ToArray(),
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => calendar.Create(CalendarTestData.Owner, draft, ids));
        }
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        contacts.SetBlocked(CalendarTestData.Guest, [CalendarTestData.Owner], true);
        Assert.Throws<InvalidDataException>(() => calendar.Create(CalendarTestData.Owner, draft, [extras[0], CalendarTestData.Guest]));
        Assert.Empty(calendar.Month(CalendarTestData.Owner, 11, 2026));
        Assert.Equal(0, calendar.Revision);
        long id = calendar.Create(CalendarTestData.Owner, draft, extras.Take(CalendarRules.MaximumInvitees).ToArray());
        Assert.Equal(50, calendar.Attendance(CalendarTestData.Owner, id, CalendarTestData.Owner).Count);
        Assert.Throws<InvalidDataException>(() => calendar.UpdateParticipants(CalendarTestData.Owner, id, [extras[^1]], []));
        Assert.Throws<InvalidDataException>(() =>
            calendar.UpdateParticipants(CalendarTestData.Owner, id, [CalendarTestData.Guest], [extras[0]])
        );
        Assert.Equal(50, calendar.Attendance(CalendarTestData.Owner, id, CalendarTestData.Owner).Count);
        Assert.Throws<InvalidDataException>(() => calendar.UpdateParticipants(CalendarTestData.Owner, id, [extras[0]], [extras[0]]));
    }

    [Fact]
    public void OnlyTheCreatorCanMutateAndPastEventsKeepReadableHistory()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        var draft = CalendarTestData.Draft(clock);
        long id = calendar.Create(CalendarTestData.Owner, draft, [CalendarTestData.Guest]);
        Assert.Throws<InvalidDataException>(() => calendar.Edit(CalendarTestData.Guest, id, draft.Time, draft with { Title = "Stolen" }));
        Assert.Throws<InvalidDataException>(() => calendar.Delete(CalendarTestData.Guest, id, CalendarTestData.Owner));
        Assert.Throws<InvalidDataException>(() => calendar.Delete(CalendarTestData.Owner, id, CalendarTestData.Guest));
        Assert.Throws<InvalidDataException>(() => calendar.UpdateParticipants(CalendarTestData.Guest, id, [], []));
        Assert.Throws<InvalidDataException>(() =>
            calendar.Respond(CalendarTestData.Owner, id, CalendarTestData.Owner, CalendarResponse.Declined)
        );
        foreach (
            var response in new[] { CalendarResponse.Uninvited, CalendarResponse.Undecided, CalendarResponse.Deleted, (CalendarResponse)6 }
        )
        {
            Assert.Throws<InvalidDataException>(() => calendar.Respond(CalendarTestData.Guest, id, CalendarTestData.Owner, response));
        }
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.Throws<InvalidDataException>(() => calendar.Edit(CalendarTestData.Owner, id, draft.Time, draft with { Time = clock.Now }));
        Assert.Throws<InvalidDataException>(() =>
            calendar.Respond(CalendarTestData.Guest, id, CalendarTestData.Owner, CalendarResponse.Accepted)
        );
        Assert.False(Assert.Single(calendar.Month(CalendarTestData.Owner, 11, 2026)).Deleted);
        Assert.Equal(draft.Description, calendar.Details(CalendarTestData.Guest, id, CalendarTestData.Owner).Text);
    }

    [Fact]
    public void NativeDateDurationAndTextBoundariesAreEnforcedByGameplayRules()
    {
        var clock = CalendarTestData.Clock();
        var draft = CalendarTestData.Draft(clock);
        foreach (
            var invalid in new[]
            {
                draft with
                {
                    Time = long.MinValue,
                },
                draft with
                {
                    Time = long.MaxValue,
                },
                draft with
                {
                    Time = clock.Now - TimeSpan.FromMinutes(1).Ticks,
                },
                draft with
                {
                    DurationMinutes = 0,
                },
                draft with
                {
                    DurationMinutes = 90,
                },
                draft with
                {
                    DurationMinutes = 1441,
                },
                draft with
                {
                    Title = " ",
                },
                draft with
                {
                    Title = new('x', 41),
                },
                draft with
                {
                    Title = "bad\nname",
                },
                draft with
                {
                    Description = new('x', 501),
                },
                draft with
                {
                    Description = "bad\0text",
                },
                draft with
                {
                    Time = clock.UtcNow.AddMonths(13).UtcDateTime.ToFileTimeUtc(),
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => CalendarRules.Check(invalid, clock.Now));
        }
        Assert.Equal(
            new string('x', 40),
            CalendarRules
                .Check(
                    draft with
                    {
                        Title = new('x', 40),
                        Description = new('y', 500),
                        DurationMinutes = 1440,
                        Time = clock.UtcNow.AddMonths(12).UtcDateTime.ToFileTimeUtc(),
                    },
                    clock.Now
                )
                .Title
        );
        Assert.Null(CalendarRules.Check(draft with { DurationMinutes = null }, clock.Now).DurationMinutes);
        long minute = clock.Now - clock.Now % TimeSpan.TicksPerMinute;
        Assert.Equal(minute, CalendarRules.Check(draft with { Time = minute }, clock.Now).Time);
        var leap = CalendarRules.Month(2, 2028);
        Assert.Equal(TimeSpan.FromDays(29).Ticks, leap.End - leap.Start);
        Assert.Throws<InvalidDataException>(() => CalendarRules.Month(0, 2026));
        Assert.Throws<InvalidDataException>(() => CalendarRules.Month(1, 2002));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("edit")]
    [InlineData("participants")]
    [InlineData("delete")]
    public void ChangeFeedWriteFailureRollsBackTheWholeCalendarOperation(string operation)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        CalendarTestData.AddPilots(db, 1);
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        var draft = CalendarTestData.Draft(clock);
        long id = calendar.Create(CalendarTestData.Owner, draft, [CalendarTestData.Guest]);
        calendar.Respond(CalendarTestData.Guest, id, CalendarTestData.Owner, CalendarResponse.Accepted);
        var before = Assert.Single(calendar.Month(CalendarTestData.Owner, 11, 2026));
        long revision = calendar.Revision;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectCalendarChange BEFORE INSERT ON WorldCalendarChange "
                    + "BEGIN SELECT RAISE(ABORT,'forced failure'); END"
            );
        }
        Action mutation = operation switch
        {
            "create" => () => calendar.Create(CalendarTestData.Owner, draft, [CalendarTestData.Other]),
            "edit" => () =>
                calendar.Edit(CalendarTestData.Owner, id, draft.Time, draft with { Time = draft.Time + TimeSpan.FromDays(1).Ticks }),
            "participants" => () =>
                calendar.UpdateParticipants(CalendarTestData.Owner, id, [CalendarTestData.Other], [CalendarTestData.Guest]),
            _ => () => calendar.Delete(CalendarTestData.Owner, id, CalendarTestData.Owner),
        };
        Assert.Throws<DbUpdateException>(mutation);
        Assert.Equal(revision, calendar.Revision);
        Assert.Equal(before, Assert.Single(calendar.Month(CalendarTestData.Owner, 11, 2026)));
        Assert.Equal(CalendarResponse.Accepted, Assert.Single(calendar.Responses(CalendarTestData.Guest)).Response);
        Assert.Empty(calendar.Month(CalendarTestData.Other, 11, 2026));
        Assert.Single(calendar.Changes(CalendarTestData.Guest, 0).Items);
    }

    [Fact]
    public async Task CompetingReschedulesCannotOverwriteAChangedDateOrDuplicateInvitations()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        var draft = CalendarTestData.Draft(clock);
        long id = calendar.Create(CalendarTestData.Owner, draft, [CalendarTestData.Guest]);
        var errors = await Task.WhenAll(
            Enumerable
                .Range(1, 2)
                .Select(hours =>
                    Task.Run(() =>
                        Record.Exception(() =>
                            calendar.Edit(
                                CalendarTestData.Owner,
                                id,
                                draft.Time,
                                draft with
                                {
                                    Time = draft.Time + TimeSpan.FromHours(hours).Ticks,
                                }
                            )
                        )
                    )
                )
        );
        Assert.Single(errors, e => e is null);
        Assert.IsType<InvalidDataException>(Assert.Single(errors, e => e is not null));
        Assert.Equal(2, calendar.Changes(CalendarTestData.Guest, 0).Items.Count);
        Assert.Single(calendar.Attendance(CalendarTestData.Owner, id, CalendarTestData.Owner));
    }

    [Fact]
    public void FailedReplyWriteRetainsThePreviousAttendanceAndDoesNotPublishAChange()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        long id = calendar.Create(CalendarTestData.Owner, CalendarTestData.Draft(clock), [CalendarTestData.Guest]);
        long revision = calendar.Revision;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectCalendarReply BEFORE UPDATE ON WorldCalendarParticipant "
                    + "BEGIN SELECT RAISE(ABORT,'forced failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() =>
            calendar.Respond(CalendarTestData.Guest, id, CalendarTestData.Owner, CalendarResponse.Accepted)
        );
        Assert.Equal(
            CalendarResponse.Undecided,
            Assert.Single(calendar.Attendance(CalendarTestData.Owner, id, CalendarTestData.Owner)).Response
        );
        Assert.Equal(revision, calendar.Revision);
    }

    [Fact]
    public void VersionFortySevenMigrationAndOrderedChangeBatchesPreserveAllRecipients()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldCalendarChange; DROP TABLE WorldCalendarParticipant; "
                    + "DROP TABLE WorldCalendarEvent; PRAGMA user_version=47"
            );
        }
        WorldInitialization.Open(db.Path);
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        long id = calendar.Create(CalendarTestData.Owner, CalendarTestData.Draft(clock), [CalendarTestData.Guest]);
        long cursor = calendar.Observe(CalendarTestData.Guest);
        using (WorldDbContext world = new(db.Path))
        {
            var entry = world.Set<SavedCalendarEvent>().Find(id)!;
            for (int i = 0; i < CalendarRules.ChangeBatchSize + 3; i++)
            {
                world.Add(
                    new SavedCalendarChange
                    {
                        PilotId = CalendarTestData.Guest,
                        EventId = id,
                        OwnerId = CalendarTestData.Owner,
                        Time = entry.Time,
                        OldTime = entry.Time,
                        Title = $"Edit {i}",
                        Kind = CalendarChangeKind.Edited,
                        Modified = clock.Now,
                    }
                );
            }
            world.SaveChanges();
        }
        var first = calendar.Changes(CalendarTestData.Guest, cursor);
        Assert.True(first.More);
        Assert.Equal(CalendarRules.ChangeBatchSize, first.Items.Count);
        var second = calendar.Changes(CalendarTestData.Guest, first.Cursor);
        Assert.False(second.More);
        Assert.Equal(3, second.Items.Count);
        Assert.True(second.Cursor > first.Cursor);
        Assert.Equal(CalendarRules.ChangeBatchSize + 3, first.Items.Concat(second.Items).Select(c => c.Id).Distinct().Count());
        WorldInitialization.Open(db.Path);
        Assert.Single(calendar.Month(CalendarTestData.Owner, 11, 2026));
        Assert.Single(calendar.Changes(CalendarTestData.Owner, 0).Items);
        Assert.Empty(calendar.Changes(CalendarTestData.Guest, second.Cursor).Items);
    }
}
