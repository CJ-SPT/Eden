using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.Chat;
using Eden.Protocol.Corporations;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationMembershipTests
{
    private const long Ceo = 90000001;
    private const long Applicant = 90000002;
    private const int NpcCorporation = 1000044;

    private static CorporationApplicationUpdate Change(CorporationApplication application, CorporationApplicationStatus status) =>
        new(application.Id, application.PilotId, application.CorporationId, application.Text, status, "Welcome", application.AppliedAt);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfferRequiresApplicantConsentAndJoinLeavePersistWithoutMovingAssetsOrMoney(bool liveWorld)
    {
        using TestWorld db = new(liveWorld: liveWorld);
        PlayerCorporationTests.Prepare(db);
        var corporations = db.Services.GetRequiredService<IPlayerCorporations>();
        int id = corporations.Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var before = db.Store.FindPilot(1000002)!;
        var application = membership.Apply(Applicant, id, "Let me join — 探索");
        Assert.Equal(CorporationApplicationStatus.Applied, application.Status);
        Assert.Equal(NpcCorporation, db.Store.FindPilot(1000002)!.CorpId);
        Assert.Throws<InvalidDataException>(() => membership.Update(Applicant, Change(application, CorporationApplicationStatus.Joined)));
        application = membership.Update(Ceo, Change(application, CorporationApplicationStatus.Offered));
        Assert.Equal(NpcCorporation, db.Store.FindPilot(1000002)!.CorpId);
        Assert.Single(corporations.Members(Ceo));
        Assert.Throws<InvalidDataException>(() => membership.Update(Ceo, Change(application, CorporationApplicationStatus.Joined)));
        application = membership.Update(Applicant, Change(application, CorporationApplicationStatus.Joined));
        Assert.Equal(id, db.Store.FindPilot(1000002)!.CorpId);
        Assert.Equal(2, corporations.Read(id)!.MemberCount);
        Assert.Equal(0, corporations.Identity(Applicant).Roles);
        Assert.Equal(2, corporations.Employment(Applicant).Count);
        Assert.Throws<InvalidDataException>(() => membership.Update(Applicant, Change(application, CorporationApplicationStatus.Joined)));
        Assert.True(membership.CanRemove(Applicant, Applicant).Allowed);
        Assert.False(membership.CanRemove(Ceo, Ceo).Allowed);
        membership.Remove(Applicant, Applicant);
        Assert.Equal(NpcCorporation, db.Store.FindPilot(1000002)!.CorpId);
        Assert.Equal(1, corporations.Read(id)!.MemberCount);
        Assert.Equal(new[] { NpcCorporation, id, NpcCorporation }, corporations.Employment(Applicant).Select(e => e.CorporationId));
        var after = db.Store.FindPilot(1000002)!;
        Assert.Equal(before.ShipId, after.ShipId);
        Assert.Equal(before.LocationId, after.LocationId);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(0, db.Services.GetRequiredService<IPilotWalletReader>().Balance(Applicant));
        Assert.Empty(db.Services.GetRequiredService<IPilotWalletReader>().Journal(Applicant).CorporationPayments!);
        using (WorldDbContext saved = new(db.Path))
        {
            Assert.Equal(Applicant, saved.Ships.Find(before.ShipId)!.OwnerId);
        }
        db.Services.Dispose();
        WorldInitialization.Initialize(new(db.Path));
        using var reopened = ServerServices.Build(new(db.Path));
        Assert.Equal(3, reopened.GetRequiredService<IPlayerCorporations>().Employment(Applicant).Count);
        Assert.Equal(
            CorporationApplicationStatus.Joined,
            Assert.Single(reopened.GetRequiredService<ICorporationMembership>().Applications(Applicant, false)).Status
        );
        Assert.Equal(NpcCorporation, reopened.GetRequiredService<SqliteWorld>().FindPilot(1000002)!.CorpId);
    }

    [Fact]
    public void PrivateApplicationsAndMembershipMutationsRejectForgedActorsTargetsAndFullCorps()
    {
        using TestWorld db = new();
        PlayerCorporationTests.Prepare(db);
        var corporations = db.Services.GetRequiredService<IPlayerCorporations>();
        int id = corporations.Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var application = membership.Apply(Applicant, id, "Applicant's statement");
        Assert.Throws<InvalidDataException>(() => membership.Apply(Applicant, id, "Duplicate"));
        Assert.Throws<InvalidDataException>(() => membership.Apply(Ceo, id, "Already CEO"));
        Assert.Throws<InvalidDataException>(() => membership.Applications(Applicant, true));
        Assert.Throws<InvalidDataException>(() => membership.Update(Applicant, Change(application, CorporationApplicationStatus.Offered)));
        Assert.Throws<InvalidDataException>(() =>
            membership.Update(Ceo, Change(application, CorporationApplicationStatus.Offered) with { PilotId = Ceo })
        );
        Assert.Throws<InvalidDataException>(() =>
            membership.Update(Ceo, Change(application, CorporationApplicationStatus.Offered) with { CorporationId = NpcCorporation })
        );
        Assert.Throws<InvalidDataException>(() =>
            membership.Update(Ceo, Change(application, CorporationApplicationStatus.Offered) with { AppliedAt = application.AppliedAt + 1 })
        );
        Assert.Throws<InvalidDataException>(() => membership.Remove(Applicant, Ceo));
        Assert.Throws<InvalidDataException>(() => membership.Remove(Ceo, Ceo));
        Assert.Equal(application, Assert.Single(membership.Applications(Applicant, false)));
        application = membership.Update(Ceo, Change(application, CorporationApplicationStatus.Offered) with { Text = "Overwritten" });
        Assert.Equal("Applicant's statement", application.Text);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<SavedCorporation>().Find(id)!.MemberLimit = 1;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => membership.Update(Applicant, Change(application, CorporationApplicationStatus.Joined)));
        Assert.Equal(application, Assert.Single(membership.Applications(Applicant, false)));
        Assert.Equal(NpcCorporation, db.Store.FindPilot(1000002)!.CorpId);
        Assert.Single(corporations.Members(Ceo));
        Assert.Single(corporations.Employment(Applicant));
        Assert.Equal(0, corporations.Identity(Applicant).Roles);
    }

    [Theory]
    [InlineData(CorporationApplicationStatus.Withdrawn)]
    [InlineData(CorporationApplicationStatus.RejectedByCorporation)]
    [InlineData(CorporationApplicationStatus.RejectedByApplicant)]
    public void WithdrawnAndRejectedApplicationsCloseWithoutMembershipChangesAndAllowReapplication(CorporationApplicationStatus status)
    {
        using TestWorld db = new();
        PlayerCorporationTests.Prepare(db);
        int id = db.Services.GetRequiredService<IPlayerCorporations>().Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var application = membership.Apply(Applicant, id, "First");
        if (status == CorporationApplicationStatus.RejectedByApplicant)
        {
            application = membership.Update(Ceo, Change(application, CorporationApplicationStatus.Offered));
        }
        long actor = status == CorporationApplicationStatus.RejectedByCorporation ? Ceo : Applicant;
        application = membership.Update(actor, Change(application, status));
        Assert.Equal(status, application.Status);
        Assert.Throws<InvalidDataException>(() => membership.Update(actor, Change(application, status)));
        var next = membership.Apply(Applicant, id, "Second");
        Assert.NotEqual(application.Id, next.Id);
        Assert.Equal(NpcCorporation, db.Store.FindPilot(1000002)!.CorpId);
        Assert.Equal(2, membership.Applications(Applicant, false).Count);
    }

    [Fact]
    public void ClosedRecruitmentAndInvalidTextLeaveNoApplicationsOrChanges()
    {
        using TestWorld db = new();
        PlayerCorporationTests.Prepare(db);
        int id = db
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(Ceo, PlayerCorporationTests.Details() with { ApplicationsEnabled = false })
            .Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        long head = membership.Head(Applicant);
        Assert.Throws<InvalidDataException>(() => membership.Apply(Applicant, id, "Closed"));
        Assert.Throws<InvalidDataException>(() => membership.Apply(Applicant, id, new('x', 1001)));
        Assert.Throws<InvalidDataException>(() => membership.Apply(Applicant, id, "Null\0"));
        Assert.Empty(membership.Applications(Applicant, false));
        Assert.Equal(head, membership.Head(Applicant));
    }

    [Fact]
    public void FormingAnotherCorporationWithdrawsExistingOffersAndKeepsTheirHistory()
    {
        using TestWorld db = new();
        PlayerCorporationTests.Prepare(db);
        PlayerCorporationTests.Prepare(db, Applicant);
        var corporations = db.Services.GetRequiredService<IPlayerCorporations>();
        int id = corporations.Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var application = membership.Apply(Applicant, id, "Interested");
        membership.Update(Ceo, Change(application, CorporationApplicationStatus.Offered));
        var created = corporations.Create(Applicant, PlayerCorporationTests.Details("Second Corporation", "OTHER"));
        Assert.NotEqual(id, created.Corporation.Id);
        Assert.Equal(CorporationApplicationStatus.Withdrawn, Assert.Single(membership.Applications(Applicant, false)).Status);
        Assert.Equal(
            CorporationApplicationStatus.Withdrawn,
            membership.Changes(Ceo, 0).Where(c => c.Application is not null).Last().Application!.Status
        );
        Assert.Equal(created.Corporation.Id, db.Store.FindPilot(1000002)!.CorpId);
        Assert.Single(corporations.Members(Ceo));
    }

    [Fact]
    public void AcceptingOneOfTwoOffersWithdrawsTheOtherWithoutMovingItsCorporationMembers()
    {
        using TestWorld db = new();
        PlayerCorporationTests.Prepare(db);
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        long otherCeo = db.Services.GetRequiredService<ICharacterCreation>().Create(1000001, new("Other Chief", 1, 1, 1, 64, [1], [2], 18));
        PlayerCorporationTests.Prepare(db, otherCeo);
        var corporations = db.Services.GetRequiredService<IPlayerCorporations>();
        int first = corporations.Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        int second = corporations.Create(otherCeo, PlayerCorporationTests.Details("Second Corporation", "OTHER")).Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var firstApplication = membership.Apply(Applicant, first, "First");
        var secondApplication = membership.Apply(Applicant, second, "Second");
        membership.Update(Ceo, Change(firstApplication, CorporationApplicationStatus.Offered));
        membership.Update(otherCeo, Change(secondApplication, CorporationApplicationStatus.Offered));
        membership.Update(Applicant, Change(firstApplication, CorporationApplicationStatus.Joined));
        var history = membership.Applications(Applicant, false);
        Assert.Equal(CorporationApplicationStatus.Joined, history.Single(a => a.Id == firstApplication.Id).Status);
        Assert.Equal(CorporationApplicationStatus.Withdrawn, history.Single(a => a.Id == secondApplication.Id).Status);
        Assert.Equal(first, corporations.Identity(Applicant).Id);
        Assert.Equal(second, corporations.Identity(otherCeo).Id);
        Assert.Equal(2, corporations.Read(first)!.MemberCount);
        Assert.Equal(1, corporations.Read(second)!.MemberCount);
    }

    [Fact]
    public async Task ConnectedSessionsReceiveOrderedChangesOnceAndMoveChatBeforeTheNextRequest()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        PlayerCorporationTests.Prepare(db);
        int id = db.Services.GetRequiredService<IPlayerCorporations>().Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        using Client ceo = new(db, 1000001);
        using Client applicant = new(db, 1000002);
        Assert.Null(applicant.Reply("InsertApplication", [(long)id, "Hello"]));
        ceo.Advance();
        Assert.Equal(
            CorporationApplicationStatus.Applied,
            Assert.Single(ceo.Context.Notifications.OfType<CorporationApplicationChanged>()).Application.Status
        );
        ceo.Clear();
        ceo.Advance();
        Assert.Empty(ceo.Context.Notifications);
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var application = Assert.Single(membership.Applications(Applicant, false));
        var ownApplications = Assert.IsType<Dictionary<long, object?>>(applicant.Reply("GetMyApplications", []));
        var ownRow = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(ownApplications[id]))).State);
        Assert.Equal(application.Id, ownRow["applicationID"]);
        Assert.Equal(application.AppliedAt, ownRow["applicationDateTime"]);
        Assert.Equal("Hello", ownRow["applicationText"]);
        Assert.Null(ceo.Reply("UpdateApplicationOffer", UpdateArgs(application, CorporationApplicationStatus.Offered)));
        applicant.Advance();
        Assert.Equal(NpcCorporation, applicant.Context.Pilot.CorpId);
        Assert.Contains(
            applicant.Context.Notifications.OfType<CorporationApplicationChanged>(),
            n => n.Application.Status == CorporationApplicationStatus.Offered
        );
        applicant.Clear();
        var old = applicant.Context.Pilot;
        Assert.Null(applicant.Reply("UpdateApplicationOffer", UpdateArgs(application, CorporationApplicationStatus.Joined)));
        applicant.Advance();
        Assert.Equal(id, applicant.Context.Pilot.CorpId);
        Assert.Equal(id, db.Services.GetRequiredService<ChatRuntime>().User(Applicant)!.CorporationId);
        Assert.Single(applicant.Context.Notifications.OfType<CorporationMemberChanged>());
        Assert.Single(applicant.Context.Notifications.OfType<CorporationApplicationChanged>());
        applicant.Clear();
        applicant.Advance();
        Assert.Empty(applicant.Context.Notifications);
        ceo.Advance();
        Assert.Contains(ceo.Context.Notifications.OfType<CorporationCountChanged>(), n => n.After == 2);
        Assert.Contains(
            ceo.Context.Notifications.OfType<StationGuestArrived>(),
            n => n.Guest.CharacterId == Applicant && n.Guest.CorporationId == id
        );
        Assert.Contains(
            ceo.Context.Notifications.OfType<StationGuestDeparted>(),
            n => n.Guest.CharacterId == Applicant && n.Guest.CorporationId == NpcCorporation
        );
        Assert.Single(Assert.IsType<List<object?>>(applicant.Reply("GetMyOldApplications", [])));
        var page = BlueRead.Fields(Assert.IsType<BlueInstance>(ceo.Reply("GetMembersPaged", [1L])).State);
        Assert.Equal(2L, page["totalCount"]);
        Assert.Equal(0L, page["page"]);
        Assert.Equal(2, Assert.IsType<List<object?>>(page["collection"]).Count);
        Assert.False(BlueRead.Tuple(ceo.Reply("CanLeaveCurrentCorporation", []), 3)[0] as bool?);
        Assert.True(BlueRead.Tuple(applicant.Reply("CanLeaveCurrentCorporation", []), 3)[0] as bool?);
        applicant.Clear();
        Assert.Null(ceo.Reply("KickOutMember", [Applicant]));
        List<object?> output = [];
        PilotSessionOutput writer = new(
            applicant.Context,
            1000002,
            43,
            packet =>
            {
                output.Add(packet);
                return Task.CompletedTask;
            },
            _ => { }
        );
        PilotSessionTick tick = new(
            applicant.Clock,
            applicant.Coordinator,
            applicant.Services.GetRequiredService<Eden.Protocol.Skills.SkillHandler>(),
            db.Services.GetRequiredService<Eden.Gameplay.Mining.IAsteroidField>()
        );
        await tick.AdvanceAsync(applicant.Context, writer, _ => { }, 1000002, 43);
        Assert.Equal(NpcCorporation, applicant.Context.Pilot.CorpId);
        Assert.Equal(NpcCorporation, db.Services.GetRequiredService<ChatRuntime>().User(Applicant)!.CorporationId);
        Assert.Contains(output.OfType<BlueInstance>(), p => p.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal));
        Assert.Equal(old.CorpId, applicant.Context.Pilot.CorpId);
        Assert.Equal(old.ShipId, applicant.Context.Pilot.ShipId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpaceMembershipChangesPublishNewCorporationToExistingObserversWithoutResettingMotion(bool liveWorld)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: liveWorld);
        PlayerCorporationTests.Prepare(db);
        int id = db.Services.GetRequiredService<IPlayerCorporations>().Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        db.Store.SetPosition(1000000000001, new(1_000_000_000, 0, 0));
        db.Store.SetPosition(1000000000002, new(1_000_001_000, 0, 0));
        using Client ceo = new(db, 1000001);
        using Client applicant = new(db, 1000002);
        ceo.Context.Flight.Snapshot();
        applicant.Context.Flight.Snapshot();
        ceo.Context.Flight.RefreshPeers();
        ceo.Context.Flight.Updates.Clear();
        applicant.Context.Flight.Updates.Clear();
        var before = applicant.Context.Pilot.Position;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        var application = membership.Apply(Applicant, id, "Explorer");
        membership.Update(Ceo, Change(application, CorporationApplicationStatus.Offered));
        applicant.Reply("UpdateApplicationOffer", UpdateArgs(application, CorporationApplicationStatus.Joined));
        ceo.Context.Flight.RefreshPeers();
        var slim = Assert.Single(
            ceo.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.OnSlimItemChange>(),
            c => c.Id == applicant.Context.Pilot.ShipId
        );
        Assert.Equal(id, Assert.IsType<ShipSlimItem>(slim.Item).Ship.Pilot.CorpId);
        Assert.Equal(before, applicant.Context.Pilot.Position);
        Assert.DoesNotContain(ceo.Context.Flight.Updates, u => u.Command is DestinyCommand.SetState);
        Assert.Equal(
            id,
            Assert
                .IsType<ShipSlimItem>(
                    Assert.Single(applicant.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.OnSlimItemChange>()).Item
                )
                .Ship.Pilot.CorpId
        );
    }

    [Fact]
    public void Version72UpgradesPreservingCorporationCeoAndEmployment()
    {
        using TestWorld db = new();
        PlayerCorporationTests.Prepare(db);
        int id = db.Services.GetRequiredService<IPlayerCorporations>().Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldCorporationChange;
                DROP TABLE WorldCorporationApplication;
                PRAGMA user_version=72;
                """
            );
        }
        WorldInitialization.Initialize(new(db.Path));
        using WorldDbContext migrated = new(db.Path);
        Assert.Equal(74, migrated.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(Ceo, migrated.Set<SavedCorporation>().Find(id)!.CeoId);
        Assert.Equal(id, migrated.Pilots.Find(Ceo)!.CorporationId);
        Assert.Equal(2, migrated.Set<SavedCorporationEmployment>().Count(e => e.PilotId == Ceo));
        Assert.Empty(migrated.Set<SavedCorporationApplication>());
    }

    [Fact]
    public void RenegotiationAndRepeatedMembershipCyclesKeepOrderedChangesAndExpectedCounts()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        PlayerCorporationTests.Prepare(db);
        var corporations = db.Services.GetRequiredService<IPlayerCorporations>();
        int id = corporations.Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        var membership = db.Services.GetRequiredService<ICorporationMembership>();
        long cursor = membership.Head(Applicant);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            clock.Seconds++;
            var application = membership.Apply(Applicant, id, new('x', CorporationMembershipRules.MaximumApplicationLength));
            application = membership.Update(Ceo, Change(application, CorporationApplicationStatus.RenegotiatedByCorporation));
            application = membership.Update(Applicant, Change(application, CorporationApplicationStatus.RenegotiatedByApplicant));
            application = membership.Update(Ceo, Change(application, CorporationApplicationStatus.Offered));
            membership.Update(Applicant, Change(application, CorporationApplicationStatus.Joined));
            Assert.Equal(2, corporations.Read(id)!.MemberCount);
            membership.Remove(Ceo, Applicant);
            Assert.Equal(1, corporations.Read(id)!.MemberCount);
            var changes = membership.Changes(Applicant, cursor);
            Assert.Equal(7, changes.Count);
            Assert.Equal(changes.OrderBy(c => c.Id).Select(c => c.Id), changes.Select(c => c.Id));
            Assert.Equal(
                new[]
                {
                    CorporationApplicationStatus.Applied,
                    CorporationApplicationStatus.RenegotiatedByCorporation,
                    CorporationApplicationStatus.RenegotiatedByApplicant,
                    CorporationApplicationStatus.Offered,
                    CorporationApplicationStatus.Joined,
                },
                changes.Where(c => c.Application is not null).Select(c => c.Application!.Status)
            );
            Assert.Equal(
                new[] { id, NpcCorporation },
                changes.Where(c => c.Membership is not null).Select(c => c.Membership!.CorporationId)
            );
            cursor = changes[^1].Id;
            Assert.Empty(membership.Changes(Applicant, cursor));
        }
        Assert.Equal(7, corporations.Employment(Applicant).Count);
    }

    [Theory]
    [InlineData(90000001L)]
    [InlineData(90000002L)]
    public void MalformedMembershipCallsCannotSelectActorsOrBypassPermissions(long accountPilot)
    {
        using TestWorld db = new();
        PlayerCorporationTests.Prepare(db);
        int id = db.Services.GetRequiredService<IPlayerCorporations>().Create(Ceo, PlayerCorporationTests.Details()).Corporation.Id;
        using Client client = new(db, accountPilot == Ceo ? 1000001 : 1000002);
        foreach (
            var (method, args) in new (string, object?[])[]
            {
                ("InsertApplication", [(long)id, "Hello", Applicant]),
                ("InsertApplication", [long.MaxValue, "Hello"]),
                ("UpdateApplicationOffer", [1L, Applicant, (long)id, "Hello", 4294967298L, null, null]),
                ("KickOutMember", [Ceo]),
                ("GetMembersPaged", [0L]),
                ("GetMembersByIds", [new List<object?> { "90000001" }]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(client.Call(method, args));
        }
        Assert.Equal(id, db.Store.FindPilot(1000001)!.CorpId);
        Assert.Equal(NpcCorporation, db.Store.FindPilot(1000002)!.CorpId);
        Assert.Empty(db.Services.GetRequiredService<ICorporationMembership>().Applications(Applicant, false));
    }

    internal static object?[] UpdateArgs(CorporationApplication application, CorporationApplicationStatus status) =>
        [application.Id, application.PilotId, (long)application.CorporationId, application.Text, (long)status, null, application.AppliedAt];

    private sealed class Client : IDisposable
    {
        private readonly IServiceScope _scope;
        public IServiceProvider Services => _scope.ServiceProvider;
        public TestClock Clock { get; }
        public PilotCallContext Context { get; }
        public PilotUpdateCoordinator Coordinator => Services.GetRequiredService<PilotUpdateCoordinator>();

        public Client(TestWorld db, long account)
        {
            _scope = db.Services.CreateScope();
            Clock = db.Services.GetRequiredService<TimeProvider>() as TestClock ?? new();
            var pilot = db.Store.FindPilot(account)!;
            Context = new(
                account,
                pilot.Account,
                [pilot],
                Services.GetRequiredService<FlightSessionFactory>().Create,
                account == 1000001 ? 42 : 43,
                SignedSettings.Empty
            );
            Context.Select(pilot);
            Coordinator.Enter(Context);
            Context.Notifications.Clear();
        }

        public PilotCallResult Call(string method, object?[] args) =>
            Services
                .GetRequiredService<PilotCallRouter>()
                .Call(Context, "corpRegistry", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { });

        public object? Reply(string method, object?[] args) =>
            BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(Call(method, args)).Reply));

        public void Advance()
        {
            Clock.Seconds++;
            Coordinator.Advance(Context);
        }

        public void Clear() => Context.Notifications.Clear();

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
