using Eden.Data.World.Market;
using Eden.Gameplay.Calendar;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.Calendar;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CalendarContactPolicyTests
{
    private const long Owner = CalendarTestData.Owner;
    private const long Guest = CalendarTestData.Guest;
    private const int OtherCorporation = 1000035;

    [Fact]
    public async Task NativeCalendarInvitationChargesCommitWithTheEventAndSurviveReopen()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var live = Prepare(db, 5_000);
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        long id;
        using (CourierServiceTests.Client owner = new(db, clock, 1000001))
        using (CourierServiceTests.Client guest = new(db, clock, 1000002))
        {
            owner.Refresh();
            guest.Refresh();
            Assert.Null(Reply(guest.Call("SetContactCost", [75L], "account")));
            long time = CalendarTestData.Draft(clock).Time;
            Quote(Create(owner, time, Guest), 75);
            Assert.IsType<PilotCallResult.Failure>(Create(owner, time, Guest, 75));
            Assert.Empty(calendar.Month(Owner, 11, 2026));
            Assert.Empty(calendar.Month(Guest, 11, 2026));
            Assert.Equal(50d, Reply(owner.Call("GetCashBalance", [], "account")));
            guest.Advance();
            Assert.Empty(guest.Context.Notifications.OfType<CalendarEventCreated>());

            using (var world = live.Economy.Begin(new(pilots: [Owner])))
            {
                world.Set<PilotWallet>().Find(Owner)!.Cents = 10_000;
                world.Commit();
            }
            id = Assert.IsType<long>(Reply(Create(owner, time, Guest, 75)));
            Assert.Equal(25d, Reply(owner.Call("GetCashBalance", [], "account")));
            guest.Advance();
            Assert.Equal(id, Assert.Single(guest.Context.Notifications.OfType<CalendarEventCreated>()).Event.Id);
        }
        await live.FlushAsync();
        db.Services.Dispose();
        using var reopened = ServerServices.Build(new(db.Path), clock, liveWorld: true);
        calendar = reopened.GetRequiredService<IPilotCalendar>();
        Assert.Equal(id, Assert.Single(calendar.Month(Guest, 11, 2026)).Id);
        Assert.Equal(new CalendarAttendance(Guest, CalendarResponse.Undecided), Assert.Single(calendar.Attendance(Owner, id, Owner)));
        Assert.Equal(2_500L, reopened.GetRequiredService<IPilotWalletReader>().Balance(Owner));
        var payment = Assert.Single(reopened.GetRequiredService<ICorporationWallet>().Journal(Owner, false, (int)WalletAccountKey.Cash));
        Assert.Equal(WalletEntryType.Cspa, payment.EntryType);
        Assert.Equal(-7_500L, payment.AmountCents);
        Assert.Equal(Guest, payment.ReferenceId);
    }

    [Fact]
    public void NativeParticipantUpdatesChargeOnlyNewInviteesAndKeepRejectedRemovals()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        long existing = Assert.Single(CalendarTestData.AddPilots(db, 1));
        Prepare(db, 10_000);
        using CourierServiceTests.Client owner = new(db, clock, 1000001);
        using CourierServiceTests.Client guest = new(db, clock, 1000002);
        owner.Refresh();
        guest.Refresh();
        long time = CalendarTestData.Draft(clock).Time;
        long id = Assert.IsType<long>(Reply(Create(owner, time, existing)));
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        Assert.Null(Reply(guest.Call("SetContactCost", [30L], "account")));
        Quote(Update(add: [Guest], remove: [existing]), 30);

        Assert.Null(Reply(owner.Call("EditPersonalEvent", [id, time, time, 60L, "Updated event", "New text", false], "calendarMgr")));
        Assert.Null(Reply(guest.Call("SetContactCost", [50L], "account")));
        Quote(Update(add: [Guest], remove: [existing], approval: 30), 50);
        Assert.Equal(existing, Assert.Single(calendar.Attendance(Owner, id, Owner)).PilotId);
        Assert.Equal("Updated event", Assert.Single(calendar.Month(Owner, 11, 2026)).Title);
        Assert.Equal(100d, Reply(owner.Call("GetCashBalance", [], "account")));

        Assert.Null(Reply(Update(add: [Guest], remove: [existing], approval: 50)));
        Assert.Equal(Guest, Assert.Single(calendar.Attendance(Owner, id, Owner)).PilotId);
        Assert.Equal("Updated event", Assert.Single(calendar.Month(Guest, 11, 2026)).Title);
        Assert.Null(Reply(Update(add: [Guest], remove: [existing], approval: 50)));
        Assert.Null(Reply(Update(add: [], remove: [Guest])));
        Assert.Null(Reply(guest.Call("SetContactCost", [null], "account")));
        Assert.IsType<PilotCallResult.Failure>(Update(add: [Guest], remove: []));
        Assert.Empty(calendar.Attendance(Owner, id, Owner));
        Assert.Equal(50d, Reply(owner.Call("GetCashBalance", [], "account")));
        var payment = Assert.Single(db.Services.GetRequiredService<ICorporationWallet>().Journal(Owner, false, (int)WalletAccountKey.Cash));
        Assert.Equal(-5_000L, payment.AmountCents);

        PilotCallResult Update(IReadOnlyList<long> add, IReadOnlyList<long> remove, long? approval = null) =>
            owner.Call(
                "UpdateEventParticipants",
                [id, add.Cast<object?>().ToList(), remove.Cast<object?>().ToList()],
                "calendarMgr",
                Keywords(approval)
            );
    }

    private static LiveWorld Prepare(TestWorld db, long balance)
    {
        var live = db.Services.GetRequiredService<LiveWorld>();
        using var world = live.Economy.Begin(new(pilots: [Owner, Guest]));
        world.Pilots.Find(Guest)!.CorporationId = OtherCorporation;
        world.Add(new PilotWallet { PilotId = Owner, Cents = balance });
        world.Commit();
        return live;
    }

    private static PilotCallResult Create(CourierServiceTests.Client client, long time, long invitee, long? approval = null) =>
        client.Call(
            "CreatePersonalEvent",
            [time, 60L, "Mining", "Fleet", false, new List<object?> { invitee }],
            "calendarMgr",
            Keywords(approval)
        );

    private static Dictionary<string, object?>? Keywords(long? approval) =>
        approval is long cost ? new() { ["approvedCost"] = cost } : null;

    private static void Quote(PilotCallResult result, long cost)
    {
        var quote = Assert.IsType<PilotCallResult.NamedFailure>(result);
        Assert.Equal("ContactCostNotApproved", quote.Name);
        var values = BlueRead.Fields(BlueMarshal.Decode(BlueMarshal.Encode(quote.Values)));
        Assert.Equal(cost, values["totalCost"]);
        Assert.EndsWith(" ISK", BlueMarshal.Text(values["totalCostISK"]));
    }

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));
}
