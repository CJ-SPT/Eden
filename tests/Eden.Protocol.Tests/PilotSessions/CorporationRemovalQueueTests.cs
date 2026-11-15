using Eden.Data.World.Corporations;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationRemovalQueueTests
{
    private const long Ceo = 90000001;
    private const long Member = 90000002;
    private const long MemberShip = 1000000000002;
    private const int NpcCorporation = 1000044;

    [Fact]
    public async Task NativeKickInSpacePersistsUntilDockingAndPublishesDepartureExactlyOnce()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var removals = world.Services.GetRequiredService<ICorporationMemberRemovals>();
        var companies = world.Services.GetRequiredService<IPlayerCorporations>();
        world.Store.SetPosition(MemberShip, new(100, 200, 300));
        var before = world.Store.FindPilot(1000002)!;
        long head = membership.Head(Ceo);
        long memberHead = membership.Head(Member);
        long requestedAt = clock.Now;
        using (CorporationTestClient ceo = new(world, 1000001))
        {
            ceo.Context.Notifications.Clear();
            Assert.True(Assert.IsType<bool>(ceo.Reply("CanBeKickedOut", [Member])));
            Assert.Null(ceo.Reply("KickOutMember", [Member]));
            clock.AdvanceWallTime(TimeSpan.FromHours(1));
            Assert.Null(ceo.Reply("KickOutMember", [Member]));
            Assert.Equal(
                requestedAt,
                world
                    .Services.GetRequiredService<LiveWorld>()
                    .Economy.Inspect(action => action.Set<SavedCorporationRemoval>().Find(Member)!.RequestedAt)
            );
            var rows = Assert.IsType<List<object?>>(ceo.Reply("GetPendingAutoKicks", []));
            Assert.Equal(new object?[] { Member, Ceo }, Assert.IsType<object?[]>(Assert.Single(rows)));
            Assert.Equal(corporation, companies.Identity(Member).Id);
            Assert.Equal(head, membership.Head(Ceo));
            Assert.Empty(ceo.Context.Notifications.OfType<CorporationMemberChanged>());
            Assert.Equal(before.Position, world.Store.FindPilot(1000002)!.Position);
            Assert.Equal(2, companies.Employment(Member).Count);
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var resumed = reopened.GetRequiredService<ICorporationMemberRemovals>();
        Assert.Single(resumed.Pending(Ceo));
        reopened.GetRequiredService<Eden.World.Docking.SqliteWorld>().SetPosition(MemberShip, null);
        resumed.Advance();
        Assert.Empty(resumed.Pending(Ceo));
        Assert.Equal(NpcCorporation, reopened.GetRequiredService<IPlayerCorporations>().Identity(Member).Id);
        var resumedMembership = reopened.GetRequiredService<ICorporationMembership>();
        var change = Assert.Single(resumedMembership.Changes(Member, memberHead), row => row.Membership is not null);
        Assert.Equal((corporation, NpcCorporation), (change.Membership!.PreviousCorporationId, change.Membership.CorporationId));
        long after = resumedMembership.Head(Member);
        resumed.Advance();
        Assert.Equal(after, resumedMembership.Head(Member));
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
        using WorldDbContext saved = new(world.Path);
        Assert.Empty(saved.Set<SavedCorporationRemoval>());
        Assert.Equal(3, saved.Set<SavedCorporationEmployment>().Count(row => row.PilotId == Member));
    }

    [Fact]
    public void ConnectedSubjectReceivesNoDepartureUntilDockingThenRefreshesOnce()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world.Store.SetPosition(MemberShip, new(1, 2, 3));
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        member.Context.Notifications.Clear();
        Assert.Null(ceo.Reply("KickOutMember", [Member]));
        member.Advance();
        Assert.Equal(corporation, member.Context.Pilot.Identity.CorporationId);
        Assert.Empty(member.Context.Notifications.OfType<CorporationMemberChanged>());
        world.Store.SetPosition(MemberShip, null);
        world.Services.GetRequiredService<ICorporationMemberRemovals>().Advance();
        member.Advance();
        Assert.Equal(NpcCorporation, member.Context.Pilot.Identity.CorporationId);
        Assert.Single(member.Context.Notifications.OfType<CorporationMemberChanged>());
        member.Context.Notifications.Clear();
        member.Advance();
        Assert.Empty(member.Context.Notifications.OfType<CorporationMemberChanged>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ChangedAuthorityRolesSharesOrMembershipCancelAnOldRequest(int change)
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world.Store.SetPosition(MemberShip, new(1, 2, 3));
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        membership.Remove(Ceo, Member);
        var removals = world.Services.GetRequiredService<ICorporationMemberRemovals>();
        Assert.Single(removals.Pending(Ceo));
        switch (change)
        {
            case 0:
                using (CorporationTestClient ceo = new(world, 1000001))
                {
                    Assert.Null(
                        ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new((long)CorporationRole.Accountant)))
                    );
                }
                break;
            case 1:
                world.Services.GetRequiredService<ICorporationShares>().Transfer(Ceo, new(corporation, true, Member, 600));
                break;
            case 2:
                membership.Remove(Member, Member);
                JoinAgain(membership, corporation);
                break;
            default:
                using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
                {
                    action.Set<SavedCorporation>().Find(corporation)!.CeoId = Member;
                    action.Commit();
                }
                break;
        }
        removals.Advance();
        world.Store.SetPosition(MemberShip, null);
        removals.Advance();
        Assert.Equal(corporation, world.Services.GetRequiredService<IPlayerCorporations>().Identity(Member).Id);
        using var inspect = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
        Assert.Empty(inspect.Set<SavedCorporationRemoval>());
    }

    [Fact]
    public void RevokingTheRequestingPersonnelManagersAuthorityCancelsTheQueue()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        long target = CreateMember(world);
        using CorporationTestClient ceo = new(world, 1000001);
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new((long)CorporationRole.PersonnelManager))));
        long targetShip = world
            .Services.GetRequiredService<LiveWorld>()
            .Economy.Inspect(action => action.Pilots.Find(target)!.ActiveShip.Id);
        world.Store.SetPosition(targetShip, new(1, 2, 3));
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        membership.Remove(Member, target);
        Assert.Single(world.Services.GetRequiredService<ICorporationMemberRemovals>().Pending(Ceo));
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new())));
        var removals = world.Services.GetRequiredService<ICorporationMemberRemovals>();
        removals.Advance();
        world.Store.SetPosition(targetShip, null);
        removals.Advance();
        Assert.Empty(removals.Pending(Ceo));
        Assert.Equal(corporation, world.Services.GetRequiredService<IPlayerCorporations>().Identity(target).Id);
    }

    [Fact]
    public void AuthorizedRetryReplacesARequestFromARevokedManager()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        long target = CreateMember(world);
        long ship = world.Services.GetRequiredService<LiveWorld>().Economy.Inspect(action => action.Pilots.Find(target)!.ActiveShip.Id);
        world.Store.SetPosition(ship, new(1, 2, 3));
        using CorporationTestClient ceo = new(world, 1000001);
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new((long)CorporationRole.PersonnelManager))));
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        membership.Remove(Member, target);
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new())));
        clock.AdvanceWallTime(TimeSpan.FromHours(1));
        membership.Remove(Ceo, target);
        var removals = world.Services.GetRequiredService<ICorporationMemberRemovals>();
        Assert.Equal(Ceo, Assert.Single(removals.Pending(Ceo)).ActorId);
        world.Store.SetPosition(ship, null);
        removals.Advance();
        Assert.Equal(NpcCorporation, world.Services.GetRequiredService<IPlayerCorporations>().Identity(target).Id);
    }

    [Fact]
    public void OrdinaryAndSanctionedQueuesShareTheNativePanelWithoutDuplicates()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world.Store.SetPosition(MemberShip, new(1, 2, 3));
        world.Services.GetRequiredService<ICorporationMembership>().Remove(Ceo, Member);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long vote = voting.Create(Ceo, new("Expel", "", CorporationVoteType.Expel, 1, ["Yes", "No"], Member));
        voting.Cast(Ceo, corporation, vote, 0);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        voting.Activate(Ceo, vote);
        using CorporationTestClient ceo = new(world, 1000001);
        var pending = Assert.IsType<List<object?>>(ceo.Reply("GetPendingAutoKicks", []));
        Assert.Equal(new object?[] { Member, Ceo }, Assert.IsType<object?[]>(Assert.Single(pending)));
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("GetPendingAutoKicks", [1L]));
        using CorporationTestClient member = new(world, 1000002);
        Assert.IsType<PilotCallResult.Failure>(member.Call("GetPendingAutoKicks", []));
        world.Store.SetPosition(MemberShip, null);
        Assert.Empty(world.Services.GetRequiredService<ICorporationMemberRemovals>().Pending(Ceo));
        Assert.Equal(NpcCorporation, world.Services.GetRequiredService<IPlayerCorporations>().Identity(Member).Id);
        Assert.True(voting.Read(Ceo, corporation, vote).Data.HoldReleased);
    }

    [Fact]
    public void MixedBulkRemovalReportsAcceptedQueuesAndImmediateDeparturesTogether()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        long docked = CreateMember(world);
        world.Store.SetPosition(MemberShip, new(1, 2, 3));
        using CorporationTestClient ceo = new(world, 1000001);
        var result = BlueRead.Fields(ceo.Reply("KickOutMembers", [new List<object?> { Member, docked, 42L, Member }]));
        Assert.Equal(new object?[] { Member, docked }, Assert.IsType<List<object?>>(result["kicked"]));
        Assert.Equal(new object?[] { 42L }, Assert.IsType<List<object?>>(result["notKicked"]));
        var companies = world.Services.GetRequiredService<IPlayerCorporations>();
        Assert.Equal(corporation, companies.Identity(Member).Id);
        Assert.Equal(NpcCorporation, companies.Identity(docked).Id);
        Assert.Equal(2, companies.Members(Ceo).Count);
        Assert.Equal(Member, Assert.Single(world.Services.GetRequiredService<ICorporationMemberRemovals>().Pending(Ceo)).PilotId);
    }

    [Fact]
    public void FailedBatchRollsBackEarlierQueueEntriesAndPublishesNoMembershipChanges()
    {
        using TestWorld world = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        long broken = CreateMember(world);
        world.Store.SetPosition(MemberShip, new(1, 2, 3));
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        long head = membership.Head(Ceo);
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            // A missing fallback is rejected before a persistent queue can be committed.
            action.RemoveRange(
                action.Set<SavedCorporationEmployment>().Where(row => row.PilotId == broken && row.CorporationId == NpcCorporation)
            );
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => membership.RemoveMany(Ceo, [Member, broken]));
        Assert.Equal(head, membership.Head(Ceo));
        Assert.Empty(world.Services.GetRequiredService<ICorporationMemberRemovals>().Pending(Ceo));
    }

    [Fact]
    public void WarmedQueueCreationWaitingReadsAndSettlementUseNoSqlOrFittingWork()
    {
        using TestWorld world = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        world.Store.SetPosition(MemberShip, new(1, 2, 3));
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var removals = world.Services.GetRequiredService<ICorporationMemberRemovals>();
        _ = membership.CanRemove(Ceo, Member);
        _ = removals.Pending(Ceo);
        using var metrics = SessionPerformance.Begin("resident ordinary removal queue", _ => { }, force: true)!;
        Assert.Equal(new[] { Member }, membership.RemoveMany(Ceo, [Member, Member]).Kicked);
        for (int cycle = 0; cycle < 10; cycle++)
        {
            removals.Advance();
            Assert.Single(removals.Pending(Ceo));
        }
        world.Store.SetPosition(MemberShip, null);
        removals.Advance();
        Assert.Empty(removals.Pending(Ceo));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void Version166UpgradesQueueAndReopensItsCheckpoint()
    {
        using TestWorld world = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        world.Services.Dispose();
        using (WorldDbContext old = new(world.Path))
        {
            old.Database.ExecuteSqlRaw("DROP TABLE WorldCorporationRemoval; PRAGMA user_version=166;");
        }
        WorldInitialization.Initialize(new(world.Path));
        using (var reopened = ServerServices.Build(new(world.Path), liveWorld: true))
        {
            reopened.GetRequiredService<Eden.World.Docking.SqliteWorld>().SetPosition(MemberShip, new(1, 2, 3));
            reopened.GetRequiredService<ICorporationMembership>().Remove(Ceo, Member);
        }
        using var resumed = ServerServices.Build(new(world.Path), liveWorld: true);
        Assert.Single(resumed.GetRequiredService<ICorporationMemberRemovals>().Pending(Ceo));
    }

    private static void JoinAgain(ICorporationMembership membership, int corporation)
    {
        var invitation = membership.Invite(Ceo, Member);
        membership.Update(
            Member,
            new(invitation.Id, Member, corporation, "", CorporationApplicationStatus.Joined, "", invitation.AppliedAt)
        );
    }

    private static long CreateMember(TestWorld world)
    {
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        long pilot = world
            .Services.GetRequiredService<ICharacterCreation>()
            .Create(1000001, new("Queue Target", 1, 1, 1, 64, [1], [2], 18));
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var invitation = membership.Invite(Ceo, pilot);
        membership.Update(
            pilot,
            new(invitation.Id, pilot, invitation.CorporationId, "", CorporationApplicationStatus.Joined, "", invitation.AppliedAt)
        );
        return pilot;
    }
}
