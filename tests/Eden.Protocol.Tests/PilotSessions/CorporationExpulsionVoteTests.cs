using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationExpulsionVoteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldVoteCannotRestoreRolesOrExpelANewMembership(bool sanctioned)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using (CorporationTestClient ceo = new(world, 1000001))
        {
            Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new((long)CorporationRole.Accountant))));
        }
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        long id = voting.Create(90000001, Draft());
        voting.Cast(90000001, corporation, id, 0);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        if (sanctioned)
        {
            world.Store.SetPosition(1000000000002, new(1, 2, 3));
            voting.Activate(90000001, id);
        }
        membership.Remove(90000002, 90000002);
        var invitation = membership.Invite(90000001, 90000002);
        membership.Update(
            90000002,
            new(invitation.Id, 90000002, corporation, "", CorporationApplicationStatus.Joined, "", invitation.AppliedAt)
        );
        clock.AdvanceWallTime(TimeSpan.FromDays(8));
        voting.Advance();
        Assert.Equal(corporation, world.Services.GetRequiredService<IPlayerCorporations>().Identity(90000002).Id);
        Assert.Equal(0, world.Services.GetRequiredService<IPlayerCorporations>().Identity(90000002).Roles);
        Assert.Empty(voting.PendingExpulsions(90000001));
        Assert.True(voting.Read(90000001, corporation, id).Data.HoldReleased);
        Assert.Throws<InvalidDataException>(() => voting.Activate(90000001, id));
    }

    [Fact]
    public void SanctionInSpaceQueuesUntilDockingAndSurvivesRestartWithoutRoleRestoration()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long id = voting.Create(90000001, Draft());
        voting.Cast(90000001, corporation, id, 0);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        world.Store.SetPosition(1000000000002, new(1, 2, 3));
        voting.Activate(90000001, id);
        using (CorporationTestClient ceo = new(world, 1000001))
        {
            var rows = Assert.IsType<List<object?>>(ceo.Reply("GetPendingAutoKicks", []));
            Assert.Equal(new object?[] { 90000002L, 90000001L }, Assert.IsType<object?[]>(Assert.Single(rows)));
        }
        Assert.Equal(corporation, world.Services.GetRequiredService<IPlayerCorporations>().Identity(90000002).Id);
        clock.AdvanceWallTime(TimeSpan.FromDays(8));
        voting.Advance();
        Assert.False(voting.Read(90000001, corporation, id).Data.HoldReleased);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        var resumed = reopened.GetRequiredService<ICorporationVoting>();
        resumed.Advance();
        Assert.Single(resumed.PendingExpulsions(90000001));
        Assert.Throws<InvalidDataException>(() => resumed.PendingExpulsions(90000002));
        reopened.GetRequiredService<Eden.World.Docking.SqliteWorld>().SetPosition(1000000000002, null);
        resumed.Advance();
        Assert.Empty(resumed.PendingExpulsions(90000001));
        Assert.NotEqual(corporation, reopened.GetRequiredService<IPlayerCorporations>().Identity(90000002).Id);
        long head = reopened.GetRequiredService<ICorporationMembership>().Head(90000002);
        resumed.Advance();
        Assert.Equal(head, reopened.GetRequiredService<ICorporationMembership>().Head(90000002));
        Assert.True(resumed.Read(90000001, corporation, id).Data.HoldReleased);
    }

    [Fact]
    public void ProtectedShareholderNeedsVoteAndCeoSanctionWithPersistentDeparture()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var shares = world.Services.GetRequiredService<ICorporationShares>();
        shares.Transfer(90000001, new(corporation, true, 90000002, 600));
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        Assert.False(membership.CanRemove(90000001, 90000002).Allowed);
        Assert.Throws<InvalidDataException>(() => membership.Remove(90000001, 90000002));
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new((long)CorporationRole.Accountant))));
            long id = voting.Create(90000001, Draft());
            member.Advance();
            Assert.Equal(0, member.Context.Pilot.Identity.CorporationRoles);
            Assert.NotNull(member.Context.Pilot.Identity.CorporationAccess!.AuthorityHoldVoteId);
            Assert.IsType<PilotCallResult.Failure>(
                ceo.Call("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new((long)CorporationRole.Director)))
            );
            voting.Cast(90000001, corporation, id, 0);
            Assert.Throws<InvalidDataException>(() => voting.Activate(90000001, id));
            clock.AdvanceWallTime(TimeSpan.FromDays(1));
            voting.Advance();
            Assert.Single(voting.Actions(90000001));
            Assert.Throws<InvalidDataException>(() => voting.Activate(90000002, id));
            Assert.Null(
                Assert.IsType<PilotCallResult.Success>(ceo.CallService("voteManager", "ActivateSanctionedAction", [id])).Reply.ToBlue()
            );
            member.Advance();
            Assert.NotEqual(corporation, member.Context.Pilot.Identity.CorporationId);
            Assert.Throws<InvalidDataException>(() => voting.Activate(90000001, id));
            Assert.Equal(600, Assert.Single(shares.Holdings(90000002, false)).Shares);
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        Assert.NotNull(reopened.GetRequiredService<ICorporationVoting>().Actions(90000001).Single().Data.AppliedAt);
        using WorldDbContext saved = new(world.Path);
        Assert.Null(saved.Set<SavedCorporationMemberAccess>().Find(90000002L));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrExpiredExpulsionRestoresRolesOnceAcrossRestart(bool passedButExpired)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        using (CorporationTestClient ceo = new(world, 1000001))
        {
            Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new((long)CorporationRole.Accountant))));
        }
        long id = voting.Create(90000001, Draft());
        Assert.Throws<InvalidDataException>(() => voting.Create(90000001, Draft()));
        voting.Cast(90000001, corporation, id, passedButExpired ? 0 : 1);
        world.Services.Dispose();
        clock.AdvanceWallTime(TimeSpan.FromDays(passedButExpired ? 8 : 1));
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        var restored = reopened.GetRequiredService<ICorporationVoting>();
        restored.Advance();
        Assert.True(restored.Read(90000001, corporation, id).Data.HoldReleased);
        var membership = reopened.GetRequiredService<ICorporationMembership>();
        Assert.False(membership.CanRemove(90000001, 90000002).Allowed);
        long head = membership.Head(90000002);
        restored.Advance();
        Assert.Equal(head, membership.Head(90000002));
        using WorldDbContext saved = new(world.Path);
        var access = System.Text.Json.JsonSerializer.Deserialize<CorporationMemberAccess>(
            saved.Set<SavedCorporationMemberAccess>().Find(90000002L)!.DataJson
        )!;
        Assert.Null(access.AuthorityHoldVoteId);
        Assert.True(access.Roles.Has(CorporationRole.Accountant));
        Assert.Throws<InvalidDataException>(() => restored.Activate(90000001, id));
    }

    [Fact]
    public void ShareProtectionUsesStrictAverageAndRejectsInvalidVoteTargetsAtomically()
    {
        Assert.False(CorporationVotingRules.RequiresExpulsionVote(500, 1000, 2));
        Assert.True(CorporationVotingRules.RequiresExpulsionVote(501, 1000, 2));
        Assert.False(CorporationVotingRules.RequiresExpulsionVote(long.MaxValue, long.MaxValue, 1));
        Assert.True(CorporationVotingRules.RequiresExpulsionVote(long.MaxValue, long.MaxValue, 2));
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        Assert.Throws<InvalidDataException>(() => voting.Create(90000001, Draft() with { Parameter = 90000001 }));
        Assert.Throws<InvalidDataException>(() => voting.Create(90000001, Draft() with { Parameter = 42 }));
        Assert.Empty(voting.List(90000001, corporation).Open);
    }

    private static CorporationVoteDraft Draft() => new("Expel member", "", CorporationVoteType.Expel, 1, ["Yes", "No"], 90000002);
}
