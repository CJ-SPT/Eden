using Eden.Gameplay.Alliances;
using Eden.Gameplay.Calendar;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Mail;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Alliances;
using Eden.Protocol.Calendar;
using Eden.Protocol.Corporations;
using Eden.Protocol.Mail;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Chat;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class AllianceSocialTests
{
    internal static (int Alliance, int Founder, int Member) Admit(TestWorld world, TestClock clock)
    {
        int founder = Corporation(world);
        int member = Corporation(world, Other, "Applicant Corporation", "APPL");
        int alliance = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        membership.Apply(Other, alliance, "Join");
        membership.Decide(Ceo, member, "Join", AllianceApplicationStatus.Accepted);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        return (alliance, founder, member);
    }

    internal static object? Reply(
        CorporationTestClient client,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    ) =>
        BlueMarshal.Decode(
            BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService(service, method, args, keywords)).Reply)
        );

    [Fact]
    public void DiplomatsAndCommunicationsOfficersUseTheirCurrentAllianceRoles()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Corporation(world);
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var application = membership.Apply(Other, corporation, "Application");
        CorporationApplicationUpdate Update(CorporationApplicationStatus status) =>
            new(application.Id, Other, corporation, application.Text, status, "Welcome", application.AppliedAt);
        membership.Update(Ceo, Update(CorporationApplicationStatus.Offered));
        membership.Update(Other, Update(CorporationApplicationStatus.Joined));
        int alliance = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;

        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        long bulletin = Assert.IsType<long>(Reply(ceo, "allianceRegistry", "AddBulletin", ["Orders", "Bring ships"]));
        long eventTime = clock.Now + TimeSpan.FromDays(1).Ticks;
        object?[] eventArgs = [eventTime, 60L, "Operation", "Alliance only", true];
        object?[] mailArgs = [new List<object?>(), null, (long)alliance, "Operations", "Bring ships", null, null];
        Assert.IsType<PilotCallResult.Failure>(member.CallService("calendarMgr", "CreateAllianceEvent", eventArgs));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("mailMgr", "SendMail", mailArgs));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("allianceRegistry", "DeleteBulletin", [bulletin]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("allianceRegistry", "AddAllianceContact", [Ceo, 5d]));

        ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Other, new((long)CorporationRole.Diplomat)));
        Assert.Null(Reply(member, "allianceRegistry", "AddAllianceContact", [Ceo, 5.5d]));
        long label = Assert.IsType<long>(Reply(member, "allianceRegistry", "CreateLabel", ["Diplomatic", null]));
        Assert.Null(Reply(member, "allianceRegistry", "AssignLabels", [new List<object?> { Ceo }, label]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("allianceRegistry", "SetRelationship", [0L, Ceo]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("calendarMgr", "CreateAllianceEvent", eventArgs));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("mailMgr", "SendMail", mailArgs));

        ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Other, new((long)CorporationRole.ChatManager)));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("allianceRegistry", "EditAllianceContact", [Ceo, -5d]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("allianceRegistry", "AddBulletin", ["New", "Denied"]));
        long revision = Assert.Single(world.Services.GetRequiredService<IAllianceSocial>().Read(Other).Bulletins).EditedAt;
        Assert.Equal(
            bulletin,
            Assert.IsType<long>(
                Reply(
                    member,
                    "allianceRegistry",
                    "AddBulletin",
                    ["Updated", "Bring ammunition"],
                    new() { ["bulletinID"] = bulletin, ["editDateTime"] = revision }
                )
            )
        );
        Assert.Null(Reply(member, "allianceRegistry", "DeleteBulletin", [bulletin]));
        long message = Assert.IsType<long>(Reply(member, "mailMgr", "SendMail", mailArgs));
        Assert.Equal(alliance, Assert.Single(world.Services.GetRequiredService<IPilotMail>().Headers(Ceo, [message])).OrganizationId);
        long eventId = Assert.IsType<long>(Reply(member, "calendarMgr", "CreateAllianceEvent", eventArgs));
        Assert.Null(Reply(member, "calendarMgr", "DeleteEvent", [eventId, (long)alliance]));
    }

    [Fact]
    public void AlliedDirectorsCanManageBulletinsWithTheNativeLimitAndContiguousOrder()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        using CorporationTestClient member = new(world, 1000002);
        var ids = Enumerable
            .Range(0, CorporationCommunicationRules.MaximumBulletins)
            .Select(i => Assert.IsType<long>(Reply(member, "allianceRegistry", "AddBulletin", [$"Bulletin {i}", "Alliance only"])))
            .ToArray();
        Assert.IsType<PilotCallResult.Failure>(member.CallService("allianceRegistry", "AddBulletin", ["Overflow", "Denied"]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("allianceRegistry", "AddAllianceContact", [Ceo, 5d]));

        Array.Reverse(ids);
        Assert.Null(Reply(member, "allianceRegistry", "UpdateBulletinOrder", [ids.Cast<object?>().ToList()]));
        Assert.Null(Reply(member, "allianceRegistry", "DeleteBulletin", [ids[0]]));
        long last = Assert.IsType<long>(Reply(member, "allianceRegistry", "AddBulletin", ["Replacement", "Last"]));
        var reply = Assert.IsType<AllianceBulletinsReply>(
            Assert.IsType<PilotCallResult.Success>(member.CallService("allianceRegistry", "GetBulletins", [])).Reply
        );
        var rows = Assert.IsType<List<object?>>(reply.ToBlue());
        var values = rows.Select(row =>
            {
                var constructor = Assert.IsType<BlueConstructor>(Assert.IsType<AllianceBulletinReply>(row).ToBlue());
                Assert.Equal("blue.DBRow", constructor.Name);
                return Assert.IsType<object?[]>(constructor.Arguments[1]);
            })
            .ToArray();
        Assert.Equal(ids.Skip(1).Append(last), values.Select(bulletin => Assert.IsType<long>(bulletin[0])));
        Assert.Equal(Enumerable.Range(0, ids.Length), values.Select(bulletin => Assert.IsType<int>(bulletin[6])));

        world.Services.GetRequiredService<IAllianceMembership>().Remove(Other, setup.Member);
        Assert.IsType<PilotCallResult.Failure>(member.CallService("allianceRegistry", "DeleteBulletin", [last]));
    }

    [Fact]
    public async Task SharedContactsLabelsAndBulletinsAuthorizeEditsNotifyPeersAndReopen()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        var social = world.Services.GetRequiredService<IAllianceSocial>();
        long bulletin;
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            Assert.Empty(Assert.IsType<Dictionary<string, object?>>(Reply(member, "allianceRegistry", "GetAllianceContacts", [])));
            Assert.Null(Reply(ceo, "allianceRegistry", "AddAllianceContact", [Other, 5d]));
            member.Advance();
            var update = Assert.Single(member.Context.Notifications.OfType<OrganizationContactsUpdated>());
            Assert.Equal(setup.Alliance, update.OwnerId);
            Assert.Equal(5d, Assert.Single(update.Updates).Standing);
            long label = Assert.IsType<long>(Reply(ceo, "allianceRegistry", "CreateLabel", ["友好", 3L]));
            Assert.Null(Reply(ceo, "allianceRegistry", "AssignLabels", [new List<object?> { Other }, label]));
            Assert.Equal(label, Assert.Single(social.Read(Other).Contacts).LabelMask);
            Assert.IsType<PilotCallResult.Failure>(member.CallService("allianceRegistry", "EditAllianceContact", [Ceo, -5d]));
            bulletin = Assert.IsType<long>(Reply(ceo, "allianceRegistry", "AddBulletin", ["Operations", "Bring ships"]));
            var before = Assert.Single(social.Read(Other).Bulletins);
            Assert.Equal(bulletin, before.Id);
            Assert.IsType<PilotCallResult.Failure>(
                ceo.CallService(
                    "allianceRegistry",
                    "AddBulletin",
                    ["Stale", "Not saved"],
                    new() { ["bulletinID"] = bulletin, ["editDateTime"] = before.EditedAt - 1 }
                )
            );
            Assert.Equal(
                bulletin,
                Assert.IsType<long>(
                    Reply(
                        ceo,
                        "allianceRegistry",
                        "AddBulletin",
                        ["Updated", "Bring ammunition"],
                        new() { ["bulletinID"] = bulletin, ["editDateTime"] = before.EditedAt }
                    )
                )
            );
            Assert.Equal("Updated", Assert.Single(social.Read(Other).Bulletins).Title);
            Assert.IsType<AllianceBulletinsReply>(
                Assert.IsType<PilotCallResult.Success>(member.CallService("allianceRegistry", "GetBulletins", [])).Reply
            );
            social.SetMotd(Ceo, setup.Alliance, "Welcome 星");
            long revision = social.Revision(setup.Alliance);
            Assert.Throws<InvalidDataException>(() => social.SetStanding(Ceo, [Other, long.MaxValue], -10));
            Assert.Throws<InvalidDataException>(() => social.SetLabels(Ceo, [Other], label << 1, true));
            Assert.Equal(revision, social.Revision(setup.Alliance));
            social.DeleteLabel(Ceo, label);
            Assert.Equal(0, Assert.Single(social.Read(Other).Contacts).LabelMask);
            Assert.Empty(social.Read(Other).Labels);
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var saved = reopened.GetRequiredService<IAllianceSocial>();
        Assert.Equal("Welcome 星", saved.ReadMotd(setup.Alliance));
        Assert.Equal(5d, Assert.Single(saved.Read(Other).Contacts).Standing);
        Assert.Equal("Updated", Assert.Single(saved.Read(Other).Bulletins).Title);
        long next = saved.SaveBulletin(Ceo, "Next operation", "Details", null, null);
        Assert.True(next > bulletin);
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public void AllianceChatUsesCurrentMembershipForSendDeliveryJoinAndMotd()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        var chat = world.Services.GetRequiredService<ChatRuntime>();
        Guid firstSession = Guid.NewGuid();
        Guid secondSession = Guid.NewGuid();
        chat.Update(firstSession, new(Ceo, "Founder", setup.Founder, 30000142, 0, AllianceId: setup.Alliance));
        chat.Update(secondSession, new(Other, "Member", setup.Member, 30000142, 0, AllianceId: setup.Alliance));
        var first = chat.Authenticate("90000001", chat.IssueToken(firstSession));
        var second = chat.Authenticate("90000002", chat.IssueToken(secondSession));
        ChatRoom room = new(ChatRoomKind.Alliance, setup.Alliance);
        chat.Join(first.Id, room, "first");
        chat.Join(second.Id, room, "second");
        Drain(first);
        Drain(second);
        chat.SetMotd(firstSession, room, "Secret operations");
        Assert.Equal("Secret operations", Assert.Single(Drain(second).OfType<ChatEvent.Subject>()).Text);
        Assert.Throws<InvalidDataException>(() => chat.SetMotd(secondSession, room, "Unauthorized"));
        world.Services.GetRequiredService<IAllianceMembership>().Remove(Other, setup.Member);
        // The XMPP connection deliberately retains its previous game-session identity.
        chat.Send(first.Id, room, "After departure", "message");
        Assert.DoesNotContain(Drain(second), e => e is ChatEvent.Message);
        Assert.Contains(Drain(first), e => e is ChatEvent.Message { Text: "After departure" });
        Assert.Throws<InvalidDataException>(() => chat.Join(second.Id, room, "stale join"));
        Assert.Throws<InvalidDataException>(() => chat.Send(second.Id, room, "Stale sender", "denied"));
        Assert.DoesNotContain(chat.SystemChannels(secondSession), r => r.Kind == ChatRoomKind.Alliance);
        Assert.Throws<InvalidDataException>(() => world.Services.GetRequiredService<IAllianceSocial>().Read(Other));
        chat.Disconnect(first.Id);
        chat.Disconnect(second.Id);

        static IReadOnlyList<ChatEvent> Drain(Eden.Protocol.Chat.XmppChatConnection connection)
        {
            List<ChatEvent> events = [];
            while (connection.Events.TryRead(out var delivery))
            {
                events.Add(delivery.Event);
            }
            return events;
        }
    }

    [Fact]
    public async Task AllianceMailSnapshotsRecipientsAndCalendarChecksLiveMembershipAcrossReopen()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        var mail = world.Services.GetRequiredService<IPilotMail>();
        var calendar = world.Services.GetRequiredService<IAllianceCalendar>();
        long eventId;
        long message;
        long time = clock.Now + TimeSpan.FromDays(2).Ticks;
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            var recipientMail = member.Services.GetRequiredService<MailHandler>();
            recipientMail.Enter(member.Context);
            message = Assert.IsType<long>(
                Reply(
                    ceo,
                    "mailMgr",
                    "SendMail",
                    [new List<object?>(), null, (long)setup.Alliance, "Alliance operations", "Bring ships", null, null]
                )
            );
            var header = Assert.Single(mail.Headers(Other, [message]));
            Assert.Equal(setup.Alliance, header.OrganizationId);
            Assert.Equal((long)SystemMailLabel.Alliance, Assert.Single(mail.Sync(Other, null, 0).Status).Labels);
            eventId = Assert.IsType<long>(
                Reply(ceo, "calendarMgr", "CreateAllianceEvent", [time, 60L, "Operation", "Alliance only", true])
            );
            Assert.Equal("Alliance only", calendar.Details(Other, eventId, setup.Alliance).Text);
            calendar.Respond(Other, eventId, setup.Alliance, CalendarResponse.Maybe);
            Assert.Equal(
                CalendarResponse.Maybe,
                calendar.Attendance(Ceo, eventId, setup.Alliance).Single(a => a.PilotId == Other).Response
            );
            member.Advance();
            recipientMail.Advance(member.Context);
            var delivery = Assert.Single(member.Context.Notifications.OfType<MailSent>());
            var deliveryArgs = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(delivery.Arguments)), 8);
            Assert.Equal(message, deliveryArgs[0]);
            Assert.Equal((long)setup.Alliance, deliveryArgs[5]);
            Assert.Single(member.Context.Notifications.OfType<CalendarEventCreated>());
            Assert.Null(
                Reply(member, "calendarMgr", "EditAllianceEvent", [eventId, time, time, 60L, "Updated by allied CEO", "Updated", false])
            );
            Assert.Equal("Updated", calendar.Details(Ceo, eventId, setup.Alliance).Text);
            member.Context.Notifications.Clear();
            world.Services.GetRequiredService<IAllianceMembership>().Remove(Other, setup.Member);
            Assert.Throws<InvalidDataException>(() => calendar.Details(Other, eventId, setup.Alliance));
            Assert.Throws<InvalidDataException>(() => calendar.Edit(Other, eventId, time, new(time, 60, "Unauthorized", "", false)));
            Assert.Throws<InvalidDataException>(() => calendar.Respond(Other, eventId, setup.Alliance, CalendarResponse.Accepted));
            var date = DateTime.FromFileTimeUtc(time);
            Assert.Empty(calendar.Month(Other, date.Month, date.Year));
            Assert.Throws<InvalidDataException>(() =>
                mail.Send(Other, new([], "Denied", "No longer allied", null, null) { OrganizationId = setup.Alliance }, Guid.NewGuid())
            );
            long later = mail.Send(Ceo, new([], "Later", "Members only", null, null) { OrganizationId = setup.Alliance }, Guid.NewGuid());
            Assert.Empty(mail.Headers(Other, [later]));
            Assert.Equal("Bring ships", mail.Body(Other, message, false, Guid.NewGuid()));
            calendar.Edit(Ceo, eventId, time, new(time, 60, "Updated operation", "New private text", true));
            member.Advance();
            Assert.Empty(member.Context.Notifications.OfType<CalendarEventEdited>());
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var saved = reopened.GetRequiredService<IAllianceCalendar>();
        Assert.Equal("New private text", saved.Details(Ceo, eventId, setup.Alliance).Text);
        Assert.Throws<InvalidDataException>(() => saved.Details(Other, eventId, setup.Alliance));
        Assert.Equal(setup.Alliance, Assert.Single(reopened.GetRequiredService<IPilotMail>().Headers(Other, [message])).OrganizationId);
        long personal = reopened
            .GetRequiredService<Eden.Gameplay.Calendar.IPilotCalendar>()
            .Create(Ceo, new(time, 60, "Personal event", "Personal", false), []);
        Assert.True(personal > eventId);
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public void WarmSocialViewsAndUnchangedPeerPollingUseNoSqlConnectionsOrFittingWork()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var setup = Admit(world, clock);
        var social = world.Services.GetRequiredService<IAllianceSocial>();
        social.SetStanding(Ceo, [Other], 5);
        using CorporationTestClient member = new(world, 1000002);
        member.CallService("allianceRegistry", "GetAllianceContacts", []);
        member.Advance();
        var snapshot = social.Read(Other);
        using var metrics = SessionPerformance.Begin("alliance warmed social reads", _ => { }, force: true)!;
        for (int cycle = 0; cycle < 10; cycle++)
        {
            Assert.Same(snapshot, social.Read(Other));
            Assert.Equal(setup.Alliance, world.Services.GetRequiredService<IAlliances>().PilotAffiliation(Other));
            member.Advance();
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
