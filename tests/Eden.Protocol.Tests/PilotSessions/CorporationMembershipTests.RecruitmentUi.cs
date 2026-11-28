using Eden.Gameplay.Corporations;
using Eden.Gameplay.Notifications;
using Eden.Protocol.Notifications;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CorporationMembershipTests
{
    [Fact]
    public void NativeApplicationFiltersKeepRecentDecisionsAndOpenApplicationsOnFreshReads()
    {
        TestClock clock = new(new DateTimeOffset(2026, 1, 8, 12, 0, 0, TimeSpan.Zero));
        using TestWorld db = new(clock: clock, liveWorld: true);
        PlayerCorporationTests.Prepare(db);
        int corporation = db
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(Ceo, PlayerCorporationTests.Details())
            .Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var rejected = membership.Apply(Applicant, corporation, "First");
        rejected = membership.Update(Ceo, Change(rejected, CorporationApplicationStatus.RejectedByCorporation));
        var active = membership.Apply(Applicant, corporation, "Second");
        clock.UtcNow = clock.UtcNow.AddMonths(3);

        using Client recruiter = new(db, 1000001);
        using Client applicant = new(db, 1000002);
        Assert.Equal(
            new[] { (active.Id, active.Status), (rejected.Id, rejected.Status) },
            ApplicationRows(Current(recruiter, "GetApplications", Applicant))
        );
        Assert.Equal(
            new[] { (active.Id, active.Status), (rejected.Id, rejected.Status) },
            ApplicationRows(Current(applicant, "GetMyApplications", corporation))
        );
        Assert.Empty(Assert.IsType<List<object?>>(applicant.Reply("GetMyOldApplications", [])));
        Assert.IsType<PilotCallResult.Failure>(applicant.Call("GetApplications", []));

        clock.UtcNow = clock.UtcNow.AddTicks(1);
        Assert.Equal((active.Id, active.Status), Assert.Single(ApplicationRows(Current(recruiter, "GetApplications", Applicant))));
        Assert.Equal((active.Id, active.Status), Assert.Single(ApplicationRows(Current(applicant, "GetMyApplications", corporation))));
        Assert.Equal(
            (rejected.Id, rejected.Status),
            Assert.Single(ApplicationRows(Assert.IsType<List<object?>>(recruiter.Reply("GetOldApplications", []))))
        );
        Assert.Equal(
            (rejected.Id, rejected.Status),
            Assert.Single(ApplicationRows(Assert.IsType<List<object?>>(applicant.Reply("GetMyOldApplications", []))))
        );

        var withdrawn = membership.Update(Applicant, Change(active, CorporationApplicationStatus.Withdrawn));
        Assert.Equal(
            (withdrawn.Id, withdrawn.Status),
            Assert.Single(ApplicationRows(Current(applicant, "GetMyApplications", corporation)))
        );

        static List<object?> Current(Client client, string method, long owner) =>
            Assert.IsType<List<object?>>(Assert.IsType<Dictionary<long, object?>>(client.Reply(method, []))[owner]);
    }

    [Fact]
    public async Task RecruitmentPublishesNativeInboxNoticesOnceAndKeepsOfflineNoticesAcrossRestart()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        PlayerCorporationTests.Prepare(db);
        int corporation = db
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(Ceo, PlayerCorporationTests.Details())
            .Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        InboxQuery corporationFeed = new(Group: InboxGroup.Corporation);

        using (Client recruiter = new(db, 1000001))
        using (Client applicant = new(db, 1000002))
        {
            var rejected = membership.Apply(Applicant, corporation, "First application");
            var newNotice = Push(recruiter, InboxNotificationType.CorporationApplicationReceived, Applicant);
            Assert.Equal("First application", BlueMarshal.Text(newNotice["applicationText"]));
            Assert.Empty(inbox.Read(Applicant, corporationFeed));

            var decision = Change(rejected, CorporationApplicationStatus.RejectedByCorporation) with { Response = "Try again next month" };
            membership.Update(Ceo, decision);
            var rejectedNotice = Push(applicant, InboxNotificationType.CorporationApplicationDeclinedWithMessage, corporation);
            Assert.Equal(decision.Response, BlueMarshal.Text(rejectedNotice["customMessage"]));
            Assert.Throws<InvalidDataException>(() => membership.Update(Ceo, decision));

            var invitation = membership.Invite(Ceo, Applicant);
            Assert.Equal(invitation.Id, membership.Invite(Ceo, Applicant).Id);
            var invitedNotice = Push(applicant, InboxNotificationType.CorporationApplicationInvited, corporation);
            Assert.Equal(Ceo, invitedNotice["invokingCharID"]);
            Assert.Equal(2, inbox.Read(Applicant, corporationFeed).Count);
            membership.Update(Applicant, Change(invitation, CorporationApplicationStatus.RejectedByApplicant));
            Push(recruiter, InboxNotificationType.CorporationApplicationOfferDeclined, Applicant);
        }

        var active = membership.Apply(Applicant, corporation, "Offline notice");
        membership.Update(Ceo, Change(active, CorporationApplicationStatus.Offered));
        await db.Services.GetRequiredService<LiveWorld>().FlushAsync();
        db.Services.Dispose();

        using var reopened = ServerServices.Build(new(db.Path), clock, liveWorld: true);
        var history = reopened.GetRequiredService<IPilotInbox>();
        Assert.Equal(
            new[]
            {
                InboxNotificationType.CorporationApplicationOffered,
                InboxNotificationType.CorporationApplicationInvited,
                InboxNotificationType.CorporationApplicationDeclinedWithMessage,
            },
            history.Read(Applicant, corporationFeed with { UnreadOnly = true }).Select(n => n.Type)
        );
        Assert.Equal("Offline notice", history.Read(Ceo, corporationFeed)[0].CorporationApplication!.ApplicationText);

        Dictionary<string, object?> Push(Client client, InboxNotificationType type, long sender)
        {
            client.Advance();
            var notification = Assert.Single(client.Context.Notifications.OfType<InboxNotificationReceived>());
            var args = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments)), 5);
            Assert.Equal((long)type, args[1]);
            Assert.Equal(sender, args[2]);
            var content = Assert.IsType<Dictionary<string, object?>>(args[4]);
            Assert.Equal(Applicant, content["charID"]);
            Assert.Equal((long)corporation, content["corpID"]);
            client.Clear();
            client.Advance();
            Assert.DoesNotContain(client.Context.Notifications, row => row is InboxNotificationReceived);
            client.Clear();
            return content;
        }
    }

    private static IEnumerable<(long, CorporationApplicationStatus)> ApplicationRows(List<object?> rows)
    {
        foreach (var row in rows)
        {
            var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(row).State);
            yield return (
                Assert.IsType<long>(fields["applicationID"]),
                (CorporationApplicationStatus)Assert.IsType<long>(fields["status"])
            );
        }
    }
}
