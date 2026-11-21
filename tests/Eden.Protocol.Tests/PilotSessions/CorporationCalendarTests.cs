using Eden.Gameplay.Calendar;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Calendar;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationCalendarTests
{
    private const long Ceo = 90000001;
    private const long Member = 90000002;

    [Fact]
    public async Task CorporationEventsUseCurrentRolesNotifyMembersAndReopenThroughTheNativeCalendar()
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        long time = clock.Now + TimeSpan.FromDays(1).Ticks;
        long moved = time + TimeSpan.FromDays(30).Ticks;
        long id;
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            object?[] draft = [time, null, "Fleet 星", "Meet at HQ", true];
            Assert.IsType<PilotCallResult.Failure>(member.CallService("calendarMgr", "CreateCorporationEvent", draft));
            Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new((long)CorporationRole.ChatManager))));
            id = Assert.IsType<long>(Reply(member, "calendarMgr", "CreateCorporationEvent", draft).ToBlue());
            ceo.Advance();
            Assert.Equal(corporation, Assert.Single(ceo.Context.Notifications.OfType<CalendarEventCreated>()).Event.OwnerId);
            var calendar = world.Services.GetRequiredService<ICorporationCalendar>();
            var details = Assert.IsType<CalendarDetailsReply>(Reply(ceo, "calendarProxy", "GetEventDetails", [id, (long)corporation]));
            Assert.NotEmpty(BlueMarshal.Encode(details));
            Assert.Equal(new CalendarDetails(Member, "Meet at HQ"), details.Details);
            Assert.Equal(CalendarResponse.Accepted, Assert.Single(calendar.Responses(Member)).Response);
            var month = Assert.IsType<CalendarMonthReply>(Reply(ceo, "calendarProxy", "GetEventList", [11L, 2026L]));
            Assert.Equal("Fleet 星", Assert.Single(month.Events).Title);
            Assert.IsType<PilotCallResult.Failure>(ceo.CallService("calendarProxy", "GetEventDetails", [id, Member]));
            Assert.IsType<PilotCallResult.Failure>(
                ceo.CallService("calendarMgr", "EditPersonalEvent", [id, time, time, null, "Bad", "", false])
            );
            Assert.IsType<PilotCallResult.Failure>(member.CallService("calendarMgr", "UpdateEventParticipants", [id, null, null]));
            Assert.Null(Reply(ceo, "calendarMgr", "SendEventResponse", [id, (long)corporation, (long)CalendarResponse.Maybe]).ToBlue());
            var attendees = Assert.IsType<BlueList<CalendarAttendanceRow>>(
                Reply(member, "calendarMgr", "GetResponsesToEvent", [id, (long)corporation])
            );
            Assert.Contains(attendees.Items, row => row.Attendance == new CalendarAttendance(Ceo, CalendarResponse.Maybe));
            Assert.Null(
                Reply(member, "calendarMgr", "EditCorporationEvent", [id, time, moved, 120L, "December fleet", "New HQ", false]).ToBlue()
            );
            Assert.IsType<PilotCallResult.Failure>(
                member.CallService("calendarMgr", "EditCorporationEvent", [id, time, moved, null, "Stale", "", false])
            );
            Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new())));
            Assert.IsType<PilotCallResult.Failure>(member.CallService("calendarMgr", "DeleteEvent", [id, (long)corporation]));
            ceo.Advance();
            Assert.Equal(time, Assert.Single(ceo.Context.Notifications.OfType<CalendarEventEdited>()).OldTime);
            using var metrics = SessionPerformance.Begin("corporation calendar reads", _ => { }, force: true)!;
            Assert.Empty(calendar.Month(Ceo, 11, 2026));
            Assert.Equal(moved, Assert.Single(calendar.Month(Ceo, 12, 2026)).Time);
            Assert.Equal(CalendarResponse.Undecided, Assert.Single(calendar.Responses(Ceo)).Response);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        await world.Services.GetRequiredService<LiveWorld>().FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var saved = reopened.GetRequiredService<ICorporationCalendar>();
        Assert.Equal(new CalendarDetails(Member, "New HQ"), saved.Details(Ceo, id, corporation));
        Assert.Equal("December fleet", Assert.Single(saved.Month(Member, 12, 2026)).Title);
        Assert.Equal(CalendarResponse.Undecided, saved.Attendance(Member, id, corporation).Single(a => a.PilotId == Ceo).Response);
        var personal = reopened.GetRequiredService<IPilotCalendar>().Create(Member, new(time, null, "Personal", "", false), []);
        Assert.True(personal > id);
        saved.Delete(Ceo, id, corporation);
        saved.Delete(Ceo, id, corporation);
        Assert.True(Assert.Single(saved.Month(Member, 12, 2026)).Deleted);
        Assert.Equal(CalendarResponse.Deleted, Assert.Single(saved.Responses(Member)).Response);
        Assert.Throws<InvalidDataException>(() => saved.Respond(Member, id, corporation, CalendarResponse.Maybe));
    }

    [Fact]
    public void LeavingRevokesReadsRepliesAndQueuedCalendarNotifications()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        long time = clock.Now + TimeSpan.FromDays(1).Ticks;
        long id = Assert.IsType<long>(
            Reply(ceo, "calendarMgr", "CreateCorporationEvent", [time, 60L, "Operation", "Private", false]).ToBlue()
        );
        world.Services.GetRequiredService<ICorporationMembership>().Remove(Member, Member);
        member.Advance();
        Assert.DoesNotContain(member.Context.Notifications, n => n is CalendarEventCreated);
        var date = DateTime.FromFileTimeUtc(time);
        var month = Assert.IsType<CalendarMonthReply>(Reply(member, "calendarProxy", "GetEventList", [(long)date.Month, (long)date.Year]));
        Assert.Empty(month.Events);
        Assert.Empty(
            Assert.IsType<BlueList<CalendarPersonalResponseRow>>(Reply(member, "calendarMgr", "GetResponsesForCharacter", [])).Items
        );
        Assert.IsType<PilotCallResult.Failure>(member.CallService("calendarProxy", "GetEventDetails", [id, (long)corporation]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("calendarMgr", "GetResponsesToEvent", [id, (long)corporation]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("calendarMgr", "SendEventResponse", [id, (long)corporation, 4L]));
        Assert.Null(Reply(ceo, "calendarMgr", "DeleteEvent", [id, (long)corporation]).ToBlue());
        ceo.Advance();
        Assert.True(Assert.Single(ceo.Context.Notifications.OfType<CalendarEventRemoved>()).Deleted);
        member.Advance();
        Assert.DoesNotContain(member.Context.Notifications, n => n is CalendarEventRemoved);
    }

    private static BlueModel Reply(CorporationTestClient pilot, string service, string method, object?[] args) =>
        Assert.IsType<PilotCallResult.Success>(pilot.CallService(service, method, args)).Reply;
}
