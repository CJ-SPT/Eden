using Eden.Data.World.Characters;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Notifications;
using Eden.Protocol.Chat;
using Eden.Protocol.FactionWarfare;
using Eden.Protocol.Fleets;
using Eden.Protocol.Notifications;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Alliances;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Chat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed class MilitiaOrganizationTests
{
    private const long Ceo = 90000001;
    private const long Member = 90000002;
    private const MilitiaFaction Faction = MilitiaFaction.Caldari;
    private const MilitiaOrganizationKind Corporation = MilitiaOrganizationKind.Corporation;
    private const MilitiaOrganizationKind Alliance = MilitiaOrganizationKind.Alliance;

    [Fact]
    public async Task CorporationEnrollmentAndCancelableRetirementSurviveTheDowntimeBoundary()
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(fixture);
        var organizations = fixture.Services.GetRequiredService<IMilitiaOrganizations>();
        var militia = fixture.Services.GetRequiredService<IMilitiaEnlistment>();
        militia.SetAllowedFactions(Ceo, [Faction]);
        long deadline = MilitiaHubRules.NextTransfer(clock.Now);
        using (CorporationTestClient officer = new(fixture, 1000001))
        using (CorporationTestClient member = new(fixture, 1000002))
        {
            Assert.IsType<PilotCallResult.Failure>(member.CallService("facWarMgr", "JoinFactionAsCorporation", [(long)Faction]));
            Assert.Null(Reply(officer, "JoinFactionAsCorporation", [(long)Faction]));
            var pending = BlueRead.Fields(Assert.IsType<BlueInstance>(Reply(officer, "GetCorpFactionalWarStatus", [])).State);
            Assert.Equal((long)MilitiaOrganizationPhase.Joining, pending["status"]);
            Assert.Equal((long)Faction, pending["factionID"]);
            Assert.Equal(deadline, pending["startDate"]);
            Assert.Null(Reply(member, "GetCorporationWarFactionID", [(long)corporation]));
            Assert.Throws<InvalidDataException>(() => militia.EnlistDirectly(Member, Faction));
            Assert.Null(Reply(officer, "WithdrawJoinFactionAsCorporation", [(long)Faction]));
            Assert.Equal(new(null), organizations.Status(corporation));
            Assert.Null(Reply(officer, "JoinFactionAsCorporation", [(long)Faction]));
            clock.AdvanceWallTime(TimeSpan.FromTicks(deadline - clock.Now - 1));
            organizations.Advance();
            Assert.Null(militia.EffectiveFaction(Member));
        }

        await fixture.Services.GetRequiredService<LiveWorld>().FlushAsync();
        fixture.Services.Dispose();
        using var reopened = ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        organizations = reopened.GetRequiredService<IMilitiaOrganizations>();
        militia = reopened.GetRequiredService<IMilitiaEnlistment>();
        Assert.Equal(new(Faction, MilitiaOrganizationPhase.Joining, deadline), organizations.Status(corporation));
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Throws<InvalidDataException>(() => organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.WithdrawJoin, Faction));
        Assert.Equal(new(Faction, MilitiaOrganizationPhase.Active, deadline), organizations.Status(corporation));
        Assert.Equal(new(null, Faction, 0), militia.Read(Member));
        Assert.Equal(Faction, militia.EffectiveFaction(Member));
        Assert.Equal(Faction, reopened.GetRequiredService<IPilotCharacterSheet>().ReadPublicProfile(Member)!.MilitiaFaction);
        Assert.Contains(corporation, organizations.Corporations(Faction));

        organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.Leave, Faction);
        Assert.Equal(MilitiaOrganizationPhase.Leaving, organizations.Status(corporation).Phase);
        Assert.Equal(Faction, militia.EffectiveFaction(Member));
        organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.WithdrawLeave, Faction);
        Assert.Equal(new(Faction, MilitiaOrganizationPhase.Active, deadline), organizations.Status(corporation));
        organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.Leave, Faction);
        Advance(clock, organizations);
        organizations.Advance();
        Assert.Equal(new(null), organizations.Status(corporation));
        Assert.Null(militia.EffectiveFaction(Member));
        Assert.DoesNotContain(corporation, organizations.Corporations(Faction));
    }

    [Fact]
    public async Task StandingWarningsPersistRecoverAndRemoveOnlyAnUnresolvedMember()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(fixture);
        using CorporationTestClient member = new(fixture, 1000002);
        var organizations = fixture.Services.GetRequiredService<IMilitiaOrganizations>();
        SetStanding(fixture.Services, Member, -5.01);
        Assert.Throws<InvalidDataException>(() => organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.Join, Faction));
        SetStanding(fixture.Services, Member, -5);
        organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.Join, Faction);
        SetStanding(fixture.Services, Member, -5.01);
        Advance(clock, organizations);
        Assert.Null(organizations.Status(corporation).Faction);

        SetStanding(fixture.Services, Member, -5);
        organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.Join, Faction);
        Advance(clock, organizations);
        member.Advance();
        Assert.Equal(Faction, member.Context.Pilot.Identity.WarFaction);
        Assert.Single(member.Context.Notifications.OfType<MilitiaEnlistmentUpdated>());

        SetStanding(fixture.Services, Member, -5.01);
        Advance(clock, organizations);
        var warning = Assert.Single(Inbox(fixture.Services, Member));
        Assert.Equal(InboxNotificationType.MilitiaMemberStandingWarning, warning.Type);
        var payload = BlueRead.Fields(BlueMarshal.Decode(BlueMarshal.Encode(InboxNotificationReply.Data(warning))));
        Assert.Equal(Member, payload["characterID"]);
        Assert.Equal((long)corporation, payload["corporationID"]);
        Assert.Equal(-5d, payload["requiredStanding"]);
        Assert.Equal(-5.01d, payload["characterStanding"]);
        Assert.Equal(TimeSpan.TicksPerDay, payload["warningDuration"]);

        SetStanding(fixture.Services, Member, -5);
        Advance(clock, organizations);
        Assert.Equal(corporation, fixture.Services.GetRequiredService<IPlayerCorporations>().Identity(Member).Id);
        Assert.Single(Inbox(fixture.Services, Member));
        SetStanding(fixture.Services, Member, -6);
        Advance(clock, organizations);
        Assert.Equal(2, Inbox(fixture.Services, Member).Count);
        member.Dispose();
        await fixture.Services.GetRequiredService<LiveWorld>().FlushAsync();
        fixture.Services.Dispose();
        using var reopened = ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        organizations = reopened.GetRequiredService<IMilitiaOrganizations>();
        clock.AdvanceWallTime(TimeSpan.FromTicks(TimeSpan.TicksPerDay - 1));
        organizations.Advance();
        Assert.Equal(corporation, reopened.GetRequiredService<IPlayerCorporations>().Identity(Member).Id);
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        organizations.Advance();
        organizations.Advance();
        Assert.NotEqual(corporation, reopened.GetRequiredService<IPlayerCorporations>().Identity(Member).Id);
        Assert.Null(reopened.GetRequiredService<IMilitiaEnlistment>().EffectiveFaction(Member));
        Assert.Equal(Faction, organizations.Status(corporation).Faction);
        Assert.Single(Inbox(reopened, Member), n => n.Type == InboxNotificationType.MilitiaMemberStandingRemoval);
        Assert.Throws<InvalidDataException>(() =>
            reopened.GetRequiredService<ICorporationMembership>().Apply(Member, corporation, "May I rejoin?")
        );
    }

    [Fact]
    public void MatureMemberStandingsDriveTheSeparateOrganizationWarning()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(fixture);
        var organizations = fixture.Services.GetRequiredService<IMilitiaOrganizations>();
        clock.AdvanceWallTime(MilitiaOrganizationRules.StandingMembershipAge);
        SetStanding(fixture.Services, Member, -1);
        Assert.Throws<InvalidDataException>(() => organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.Join, Faction));
        SetStanding(fixture.Services, Ceo, 1);
        organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.Join, Faction);
        Advance(clock, organizations);
        SetStanding(fixture.Services, Member, -2);
        Advance(clock, organizations);
        Assert.Equal(Faction, organizations.Status(corporation).Faction);
        var warning = Assert.Single(Inbox(fixture.Services, Ceo));
        Assert.Equal(InboxNotificationType.MilitiaCorporationStandingWarning, warning.Type);
        Assert.Equal(-0.5, warning.MilitiaStanding!.Standing);

        Advance(clock, organizations);
        Assert.Null(organizations.Status(corporation).Faction);
        Assert.Equal(corporation, fixture.Services.GetRequiredService<IPlayerCorporations>().Identity(Member).Id);
        Assert.Single(Inbox(fixture.Services, Ceo), notice => notice.Type == InboxNotificationType.MilitiaCorporationStandingRemoval);
        Assert.Empty(Inbox(fixture.Services, Member));
    }

    [Fact]
    public void AllianceRechecksApplicantEligibilityAndExecutorAuthorityAndHandlesCeoRemoval()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        int founder = AllianceCreationTests.Corporation(fixture);
        int applicant = AllianceCreationTests.Corporation(fixture, Member, "Recruit Corporation", "REC");
        var alliances = fixture.Services.GetRequiredService<IAlliances>();
        var membership = fixture.Services.GetRequiredService<IAllianceMembership>();
        var organizations = fixture.Services.GetRequiredService<IMilitiaOrganizations>();
        var militia = fixture.Services.GetRequiredService<IMilitiaEnlistment>();
        int alliance = alliances.Create(Ceo, AllianceCreationTests.Details()).Alliance.Id;
        Assert.Throws<InvalidDataException>(() => organizations.Change(Member, Alliance, MilitiaOrganizationCommand.Join, Faction));
        Assert.Throws<InvalidDataException>(() => organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.Join, Faction));
        organizations.Change(Ceo, Alliance, MilitiaOrganizationCommand.Join, Faction);
        Advance(clock, organizations);
        Assert.Equal(Faction, organizations.AllianceFaction(alliance));

        SetStanding(fixture.Services, Member, -6);
        Assert.Throws<InvalidDataException>(() => membership.Apply(Member, alliance, "Join"));
        SetStanding(fixture.Services, Member, 0);
        Apply();
        SetStanding(fixture.Services, Member, -6);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        Assert.Equal(AllianceApplicationStatus.Rejected, Assert.Single(membership.Applications(Member, false)).Status);
        Assert.Null(militia.EffectiveFaction(Member));
        SetStanding(fixture.Services, Member, 0);
        Apply();
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        Assert.Equal(Faction, militia.EffectiveFaction(Member));
        Assert.Contains(applicant, organizations.Corporations(Faction));
        Assert.Throws<InvalidDataException>(() => organizations.Change(Member, Alliance, MilitiaOrganizationCommand.Leave, Faction));
        membership.DeclareSupport(Ceo, applicant);
        membership.DeclareSupport(Member, applicant);
        Assert.Throws<InvalidDataException>(() => organizations.Change(Ceo, Alliance, MilitiaOrganizationCommand.Leave, Faction));
        organizations.Change(Member, Alliance, MilitiaOrganizationCommand.Leave, Faction);
        organizations.Change(Member, Alliance, MilitiaOrganizationCommand.WithdrawLeave, Faction);

        SetStanding(fixture.Services, Member, -6);
        Advance(clock, organizations);
        var warning = Assert.Single(Inbox(fixture.Services, Member));
        Assert.Equal(InboxNotificationType.MilitiaAllianceCeoStandingWarning, warning.Type);
        Assert.Equal(alliance, warning.MilitiaStanding!.AllianceId);
        Advance(clock, organizations);
        Assert.Null(organizations.AllianceFaction(alliance));
        Assert.Null(militia.EffectiveFaction(Ceo));
        Assert.Null(militia.EffectiveFaction(Member));
        Assert.Equal(new[] { founder, applicant }, alliances.Members(alliance).Select(row => row.CorporationId).Order().ToArray());
        Assert.Single(Inbox(fixture.Services, Member), notice => notice.Type == InboxNotificationType.MilitiaAllianceCeoStandingRemoval);

        void Apply()
        {
            membership.Apply(Member, alliance, "Join");
            membership.Decide(Ceo, applicant, "Welcome", AllianceApplicationStatus.Accepted);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MilitiaChatAndAdvertAdmissionUseCurrentAffiliationWithoutClientResync(bool approval)
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(fixture);
        var organizations = fixture.Services.GetRequiredService<IMilitiaOrganizations>();
        var militia = fixture.Services.GetRequiredService<IMilitiaEnlistment>();
        MilitiaEnlistmentTests.AddMilitiaCatalog(fixture);
        militia.SetAllowedFactions(Ceo, [Faction, MilitiaFaction.Gallente]);
        Dock(MilitiaEnlistmentTests.GallenteStation);
        militia.EnlistDirectly(Member, MilitiaFaction.Gallente);
        Assert.Throws<InvalidDataException>(() => organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.Join, Faction));
        militia.RetireDirectly(Member);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Dock(MilitiaEnlistmentTests.CaldariStation);
        militia.EnlistDirectly(Member, Faction);

        var chat = fixture.Services.GetRequiredService<ChatRuntime>();
        var fleets = fixture.Services.GetRequiredService<ICooperativeFleets>();
        ChatRoom room = new(ChatRoomKind.Militia, (int)Faction);
        Assert.Equal("faction_500001", XmppWire.Channel(room));
        Assert.Equal(room, XmppWire.ParseChannel(XmppWire.Channel(room)));
        var first = Connect(Ceo);
        var second = Connect(Member);
        Assert.Throws<InvalidDataException>(() => chat.Join(first.Id, room, null));
        chat.Join(second.Id, room, null);
        var fleet = fleets.Create(Member);
        var access = new FleetAdvertAccess(null, null, [], []);
        var settings = new FleetAdvertSettings(
            "Militia",
            "",
            FleetInviteScope.Militia,
            null,
            false,
            false,
            access,
            access,
            approval,
            false,
            true,
            null
        );
        fleets.SetAdvert(Member, settings);
        Assert.Empty(fleets.AvailableAdverts(Ceo, 0));

        organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.Join, Faction);
        Advance(clock, organizations);
        chat.Join(first.Id, room, "now-eligible");
        Assert.Equal(2, chat.Members(first.Id, room).Count);
        var joined = Assert.Single(Drain(first), delivery => delivery.CorrelationId == "now-eligible");
        var presence = Assert.IsType<ChatEvent.Presence>(joined.Event);
        Assert.Equal((int)Faction, (int?)XmppWire.UserData(presence.Pilot).Attribute("warfactionid"));
        chat.Send(second.Id, room, "Militia report", "before");
        Assert.Contains(Drain(first), delivery => delivery.Event is ChatEvent.Message { Id: "before" });
        var advert = Assert.Single(fleets.AvailableAdverts(Ceo, 0));
        var wire = new FleetApplicantReply(advert.Leader.Pilot);
        Assert.Equal(
            (long)Faction,
            BlueRead.Fields(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(wire))).State)["warFactionID"]
        );
        Assert.Equal(approval, fleets.ApplyToFleet(Ceo, fleet.Id, 0));
        if (approval)
        {
            fleets.Invite(Member, fleet.Id, Ceo, new(FleetRules.NoGroup, FleetRules.NoGroup, FleetRole.Member));
        }

        organizations.Change(Ceo, Corporation, MilitiaOrganizationCommand.Leave, Faction);
        Advance(clock, organizations);
        Assert.Equal(new(Faction, null, 0), militia.Read(Member));
        Assert.Null(militia.Read(Ceo).Effective);
        Assert.Empty(fleets.AvailableAdverts(Ceo, 0));
        Assert.Throws<InvalidDataException>(() => fleets.Accept(Ceo, fleet.Id));
        chat.Send(second.Id, room, "Updated militia report", "after");
        Assert.DoesNotContain(Drain(first), delivery => delivery.Event is ChatEvent.Message { Id: "after" });
        Assert.Throws<InvalidDataException>(() => chat.Send(first.Id, room, "No longer enlisted", "denied"));
        Assert.Equal(Member, Assert.Single(chat.Members(second.Id, room)).Id);

        void Dock(int station)
        {
            using var action = fixture.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
            action.Pilots.Find(Member)!.ActiveShip.StationId = station;
            action.Commit();
        }

        XmppChatConnection Connect(long id)
        {
            Guid session = Guid.NewGuid();
            chat.Update(session, new(id, $"Pilot {id}", corporation, 30000142, 0));
            fleets.Update(session, new(id, corporation, id + 100, 587, 30000142, null));
            return chat.Authenticate(id.ToString(System.Globalization.CultureInfo.InvariantCulture), chat.IssueToken(session));
        }
    }

    private static IEnumerable<ChatDelivery> Drain(XmppChatConnection connection)
    {
        while (connection.Events.TryRead(out var delivery))
        {
            yield return delivery;
        }
    }

    private static object? Reply(CorporationTestClient client, string method, object?[] args) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService("facWarMgr", method, args)).Reply));

    private static IReadOnlyList<InboxNotification> Inbox(IServiceProvider services, long pilot) =>
        services.GetRequiredService<IPilotInbox>().Read(pilot, new(Group: InboxGroup.FactionWarfare));

    private static void SetStanding(IServiceProvider services, long pilot, double value)
    {
        using var action = services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
        var row =
            action.Set<SavedNpcStanding>().Find(pilot, (long)Faction)
            ?? action.Add(new SavedNpcStanding { PilotId = pilot, SourceId = (int)Faction }).Entity;
        row.Value = value;
        action.Commit();
    }

    private static void Advance(TestClock clock, IMilitiaOrganizations organizations)
    {
        clock.AdvanceWallTime(TimeSpan.FromTicks(MilitiaHubRules.NextTransfer(clock.Now) - clock.Now));
        organizations.Advance();
    }
}
