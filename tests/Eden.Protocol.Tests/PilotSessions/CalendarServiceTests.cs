using Eden.Data.World.Calendar;
using Eden.Gameplay.Calendar;
using Eden.Protocol.Calendar;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Calendar;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CalendarServiceTests
{
    [Fact]
    public void AQueuedFeedLargerThanOneTickDrainsInOrderEvenWithoutAnotherPublication()
    {
        using Fixture f = new();
        f.Select();
        var calendar = f.Db.Services.GetRequiredService<IPilotCalendar>();
        long id = calendar.Create(CalendarTestData.Owner, CalendarTestData.Draft(f.Clock), [CalendarTestData.Guest]);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var entry = world.Set<SavedCalendarEvent>().Find(id)!;
            for (int i = 0; i < CalendarRules.ChangeBatchSize; i++)
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
                        Modified = f.Clock.Now,
                    }
                );
            }
            world.SaveChanges();
        }
        f.Db.Services.GetRequiredService<CalendarChangeSignal>().Publish();
        f.Guest.Advance();
        Assert.Single(f.Guest.Context.Notifications.OfType<CalendarEventCreated>());
        Assert.Equal(CalendarRules.ChangeBatchSize - 1, f.Guest.Context.Notifications.OfType<CalendarEventEdited>().Count());
        f.Guest.Context.Notifications.Clear();
        f.Guest.Advance();
        Assert.Equal(
            $"Edit {CalendarRules.ChangeBatchSize - 1}",
            Assert.Single(f.Guest.Context.Notifications.OfType<CalendarEventEdited>()).Event.Title
        );
        f.Guest.Context.Notifications.Clear();
        f.Guest.Advance();
        Assert.DoesNotContain(f.Guest.Context.Notifications, n => n is CalendarEventCreated or CalendarEventEdited);
    }

    [Fact]
    public void NativeMonthDetailsAttendanceAndReplyCallsUsePersistentSelectedPilotState()
    {
        using Fixture f = new();
        Assert.Same(PilotCallResult.Unsupported, f.Owner.Call("calendarProxy", "GetEventList", [11L, 2026L]));
        f.Select();
        long time = f.Clock.Now + TimeSpan.FromHours(2).Ticks;
        long id = Assert.IsType<long>(
            Reply(
                f.Owner.Call(
                    "calendarMgr",
                    "CreatePersonalEvent",
                    [
                        time,
                        null,
                        new BlueUnicode("Étoile 会合"),
                        new BlueUnicode("<b>説明</b>\nBring ships 🚀"),
                        1L,
                        new List<object?> { CalendarTestData.Guest },
                    ]
                )
            )
        );
        var groups = Assert.IsType<List<object?>>(RowsReply(f.Guest.Call("calendarProxy", "GetEventList", [11L, 2026L])));
        var entry = Row(Assert.Single(Assert.IsType<List<object?>>(Assert.Single(groups))));
        Assert.Equal(id, entry["eventID"]);
        Assert.Equal(CalendarTestData.Owner, entry["ownerID"]);
        Assert.Equal(time, entry["eventDateTime"]);
        Assert.Null(entry["eventDuration"]);
        Assert.Equal("Étoile 会合", entry["eventTitle"]);
        Assert.Equal(1L, entry["importance"]);
        Assert.Equal(false, entry["isDeleted"]);
        Assert.Equal(f.Clock.Now, entry["dateModified"]);
        var details = Fields(Reply(f.Guest.Call("calendarProxy", "GetEventDetails", [id, CalendarTestData.Owner])));
        Assert.Equal(CalendarTestData.Owner, details["creatorID"]);
        Assert.Equal("<b>説明</b>\nBring ships 🚀", details["eventText"]);
        var responses = Assert.IsType<List<object?>>(RowsReply(f.Guest.Call("calendarMgr", "GetResponsesForCharacter", [])));
        Assert.Equal(3L, Row(Assert.Single(responses))["status"]);
        Assert.Null(Reply(f.Guest.Call("calendarMgr", "SendEventResponse", [id, CalendarTestData.Owner, 4L])));
        var attendees = Assert.IsType<List<object?>>(
            RowsReply(f.Owner.Call("calendarMgr", "GetResponsesToEvent", [id, CalendarTestData.Owner]))
        );
        var attendee = Row(Assert.Single(attendees));
        Assert.Equal(CalendarTestData.Guest, attendee["characterID"]);
        Assert.Equal(4L, attendee["status"]);
        Assert.Null(Reply(f.Owner.Call("calendarMgr", "EditPersonalEvent", [id, time, time, 120L, "New title", "New text", false])));
        Assert.Null(Reply(f.Owner.Call("calendarMgr", "DeleteEvent", [id, CalendarTestData.Owner])));
        Assert.True(f.Db.Services.GetRequiredService<IPilotCalendar>().Month(CalendarTestData.Guest, 11, 2026)[0].Deleted);
        var ownedResponse = Row(
            Assert.Single(Assert.IsType<List<object?>>(RowsReply(f.Owner.Call("calendarMgr", "GetResponsesForCharacter", []))))
        );
        Assert.Equal(1L, ownedResponse["status"]);
    }

    [Fact]
    public void PeerNotificationsPreserveCreateEditRemoveAndCancelOrderingWithoutRepeatingOnIdleTicks()
    {
        using Fixture f = new();
        f.Select();
        long time = f.Clock.Now + TimeSpan.FromHours(2).Ticks;
        long id = Assert.IsType<long>(
            Reply(
                f.Owner.Call(
                    "calendarMgr",
                    "CreatePersonalEvent",
                    [time, 60L, "Mining", "Fleet", false, new List<object?> { CalendarTestData.Guest }]
                )
            )
        );
        Assert.Null(
            Reply(
                f.Owner.Call(
                    "calendarMgr",
                    "EditPersonalEvent",
                    [id, time, time + TimeSpan.FromDays(1).Ticks, 120L, "Moved", "New fleet", true]
                )
            )
        );
        Assert.Null(
            Reply(
                f.Owner.Call(
                    "calendarMgr",
                    "UpdateEventParticipants",
                    [id, new List<object?>(), new List<object?> { CalendarTestData.Guest }]
                )
            )
        );
        f.Guest.Advance();
        var notifications = f
            .Guest.Context.Notifications.Where(n => n is CalendarEventCreated or CalendarEventEdited or CalendarEventRemoved)
            .ToArray();
        Assert.Collection(
            notifications,
            n =>
            {
                var created = Assert.IsType<CalendarEventCreated>(n);
                Assert.Equal(
                    new object?[] { id, CalendarTestData.Owner, time, 60L, "Mining", 0L },
                    BlueMarshal.Decode(BlueMarshal.Encode(created.Arguments))
                );
            },
            n =>
            {
                var edited = Assert.IsType<CalendarEventEdited>(n);
                Assert.Equal(time, edited.OldTime);
                Assert.Equal(time + TimeSpan.FromDays(1).Ticks, edited.Event.Time);
            },
            n => Assert.False(Assert.IsType<CalendarEventRemoved>(n).Deleted)
        );
        f.Guest.Context.Notifications.Clear();
        f.Guest.Advance();
        f.Guest.Advance();
        Assert.DoesNotContain(f.Guest.Context.Notifications, n => n.Name.Contains("Calendar", StringComparison.Ordinal));
        Assert.IsType<PilotCallResult.Failure>(f.Guest.Call("calendarProxy", "GetEventDetails", [id, CalendarTestData.Owner]));
        Assert.Null(
            Reply(
                f.Owner.Call(
                    "calendarMgr",
                    "UpdateEventParticipants",
                    [id, new List<object?> { CalendarTestData.Guest }, new List<object?>()]
                )
            )
        );
        Assert.Null(Reply(f.Owner.Call("calendarMgr", "DeleteEvent", [id, CalendarTestData.Owner])));
        f.Guest.Advance();
        Assert.Single(f.Guest.Context.Notifications.OfType<CalendarEventCreated>());
        Assert.True(Assert.Single(f.Guest.Context.Notifications.OfType<CalendarEventRemoved>()).Deleted);
        Assert.DoesNotContain(f.Guest.Context.Notifications, n => n is CalendarEventEdited);
        f.Guest.Calendar.Reset();
        Assert.False(f.Guest.Calendar.IsMonitoring);
        f.Guest.Calendar.Enter(f.Guest.Context);
        f.Guest.Context.Notifications.Clear();
        f.Guest.Advance();
        Assert.DoesNotContain(f.Guest.Context.Notifications, n => n.Name.Contains("Calendar", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedForgedAndOrganizationCallsCannotCreateEventsOrChangeAnotherPilotsCalendar()
    {
        using Fixture f = new();
        f.Select();
        long time = f.Clock.Now + TimeSpan.FromHours(2).Ticks;
        foreach (
            var (service, method, args) in new (string, string, object?[])[]
            {
                ("calendarProxy", "GetEventList", []),
                ("calendarProxy", "GetEventList", [0L, 2026L]),
                ("calendarProxy", "GetEventList", [11L, long.MaxValue]),
                ("calendarProxy", "GetEventDetails", [1L]),
                ("calendarMgr", "CreatePersonalEvent", [time, 60L, "Title", "Text", "yes", null]),
                ("calendarMgr", "CreatePersonalEvent", [time, double.NaN, "Title", "Text", true, null]),
                ("calendarMgr", "CreatePersonalEvent", [time, 60L, "Title", "Text", true, new object?[] { CalendarTestData.Guest }]),
                ("calendarMgr", "SendEventResponse", [1L, CalendarTestData.Owner, long.MaxValue]),
                ("calendarMgr", "DeleteEvent", [1L, CalendarTestData.Guest]),
                ("calendarMgr", "CreateCorporationEvent", [time, 60L, "Title", "Text", true]),
                ("calendarMgr", "CreateAllianceEvent", [time, 60L, "Title", "Text", true]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(f.Owner.Call(service, method, args));
        }
        object?[] valid = [time, 60L, "Title", "Text", false, null];
        Assert.IsType<PilotCallResult.Failure>(
            f.Owner.Call("calendarMgr", "CreatePersonalEvent", valid, new() { ["ownerID"] = CalendarTestData.Guest })
        );
        Assert.IsType<PilotCallResult.Failure>(
            f.Owner.Call("calendarMgr", "CreatePersonalEvent", valid, new() { ["approvedCost"] = 100L })
        );
        Assert.Same(PilotCallResult.Unsupported, f.Owner.Call("calendarMgr", "UnknownCalendarMethod", []));
        Assert.Empty(f.Db.Services.GetRequiredService<IPilotCalendar>().Month(CalendarTestData.Owner, 11, 2026));
        long id = Assert.IsType<long>(Reply(f.Owner.Call("calendarMgr", "CreatePersonalEvent", valid, new() { ["approvedCost"] = 0L })));
        Assert.IsType<PilotCallResult.Failure>(
            f.Guest.Call("calendarMgr", "EditPersonalEvent", [id, time, time, 60L, "Stolen", "Text", false])
        );
        Assert.IsType<PilotCallResult.Failure>(f.Guest.Call("calendarMgr", "DeleteEvent", [id, CalendarTestData.Owner]));
        Assert.IsType<PilotCallResult.Failure>(f.Guest.Call("calendarMgr", "GetResponsesToEvent", [id, CalendarTestData.Owner]));
    }

    private static Dictionary<string, object?> Fields(object? value) => BlueRead.Fields(Assert.IsType<BlueInstance>(value).State);

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private static object? RowsReply(PilotCallResult result)
    {
        var reply = Assert.IsType<PilotCallResult.Success>(result).Reply;
        Assert.NotEmpty(BlueMarshal.Encode(reply));
        // The inbound decoder deliberately excludes executable DBRow reductions.
        // Inspect the native outgoing constructor, and round-trip each inert cell.
        return WireAssertions.Materialize(reply);
    }

    private static Dictionary<string, object?> Row(object? value)
    {
        var row = Assert.IsType<BlueConstructor>(value);
        Assert.Equal("blue.DBRow", row.Name);
        var descriptor = Assert.IsType<BlueConstructor>(row.Arguments[0]);
        var columns = Assert.IsType<object?[]>(descriptor.Arguments[0]);
        var values = Assert.IsType<object?[]>(row.Arguments[1]);
        return columns
            .Select(
                (column, i) =>
                    (Name: BlueMarshal.Text(Assert.IsType<object?[]>(column)[0]), Value: BlueMarshal.Decode(BlueMarshal.Encode(values[i])))
            )
            .ToDictionary(c => c.Name, c => c.Value);
    }

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = CalendarTestData.Clock();
        public TestWorld Db { get; }
        public Client Owner { get; }
        public Client Guest { get; }

        public Fixture()
        {
            Db = new(clock: Clock);
            Owner = new(Db, Clock, 1000001);
            Guest = new(Db, Clock, 1000002);
        }

        public void Select()
        {
            Owner.Select();
            Guest.Select();
            Owner.Context.Notifications.Clear();
            Guest.Context.Notifications.Clear();
        }

        public void Dispose()
        {
            Owner.Dispose();
            Guest.Dispose();
            Db.Dispose();
        }
    }

    private sealed class Client(TestWorld db, TestClock clock, long account) : IDisposable
    {
        private readonly IServiceScope _scope = db.Services.CreateScope();
        public PilotCallContext Context { get; } =
            new(
                account,
                db.Store.ReadAccount(account),
                db.Store.ListPilots(account),
                p => new(db.Store, p, clock, new(), db.Objects),
                account,
                SignedSettings.Empty
            );
        public CalendarHandler Calendar => _scope.ServiceProvider.GetRequiredService<CalendarHandler>();

        public void Select()
        {
            Context.Select(db.Store.FindPilot(account)!);
            _scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Enter(Context);
        }

        public void Advance() => _scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Advance(Context);

        public PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
        {
            var result = _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
            if (Context.Selected)
            {
                Advance();
            }
            return result;
        }

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
