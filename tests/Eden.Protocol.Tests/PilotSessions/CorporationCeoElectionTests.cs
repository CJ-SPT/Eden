using Eden.Data.World.Corporations;
using Eden.Data.World.Skills;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationCeoElectionTests
{
    private const long Ceo = 90000001;
    private const long Candidate = 90000002;

    [Fact]
    public void NativeElectionSuspendsIncumbentTransfersLeadershipAndRestoresSuccessorAfterRestart()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Prepare(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        var companies = world.Services.GetRequiredService<IPlayerCorporations>();
        long shareVote = voting.Create(Ceo, new("Issue one share", "", CorporationVoteType.Shares, 1, ["Yes", "No"], 1));
        voting.Cast(Ceo, corporation, shareVote, 0);
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            Eden.World.Corporations.CorporationRoleStore.Save(
                action,
                Ceo,
                corporation,
                new(new((long)CorporationRole.Director), TitleMask: 1)
            );
            action.Commit();
        }
        long id;
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient candidate = new(world, 1000002))
        {
            Assert.Equal(true, Reply(candidate, "CanRunForCEO", []));
            Assert.Equal(false, Reply(ceo, "CanRunForCEO", []));
            id = Assert.IsType<long>(Reply(candidate, "InsertVoteCase", ["Run for CEO", "", 0L, Options(Candidate), 1L]));
            ceo.Advance();
            Assert.Equal(0, ceo.Context.Pilot.Identity.CorporationRoles);
            Assert.Equal(id, ceo.Context.Pilot.Identity.CorporationAccess!.AuthorityHoldVoteId);
            Assert.Equal(false, Reply(candidate, "CanRunForCEO", []));
            Assert.IsType<PilotCallResult.Failure>(
                ceo.Call("UpdateMember", CorporationRoleTests.MemberArguments(Candidate, new((long)CorporationRole.Director)))
            );
            Assert.Throws<InvalidDataException>(() => world.Services.GetRequiredService<ICorporationLeadership>().Transfer(Ceo, Candidate));
            Assert.Throws<InvalidDataException>(() =>
                world.Services.GetRequiredService<ICorporationLeadership>().PayDividend(Ceo, false, 1)
            );
            Assert.Throws<InvalidDataException>(() => world.Services.GetRequiredService<ICorporationLeadership>().Close(Ceo));
            Assert.Throws<InvalidDataException>(() =>
                world.Services.GetRequiredService<ICorporationShares>().Shareholders(Ceo, corporation)
            );
            Assert.Throws<InvalidDataException>(() => voting.Create(Candidate, Draft()));
            // The suspended incumbent cannot vote the corporation treasury's shares.
            Assert.IsType<PilotCallResult.Failure>(ceo.CallService("voteManager", "InsertVote", [(long)corporation, id, 1L]));
            Assert.Null(Reply(candidate, "InsertVote", [(long)corporation, id, 0L]));
            clock.AdvanceWallTime(TimeSpan.FromDays(1) - TimeSpan.FromTicks(1));
            voting.Advance();
            Assert.Equal(Ceo, companies.Read(corporation)!.CeoId);
            clock.AdvanceWallTime(TimeSpan.FromTicks(1));
            voting.Advance();
            Assert.Equal(Candidate, companies.Read(corporation)!.CeoId);
            Assert.Equal(0, companies.Identity(Ceo).Roles);
            candidate.Advance();
            Assert.Equal(0, candidate.Context.Pilot.Identity.CorporationRoles);
            Assert.Equal(id, candidate.Context.Pilot.Identity.CorporationAccess!.AuthorityHoldVoteId);
            Assert.Throws<InvalidDataException>(() => world.Services.GetRequiredService<ICorporationLeadership>().Transfer(Candidate, Ceo));
            var vote = voting.Read(Candidate, corporation, id);
            Assert.True(vote.Data.Settled);
            Assert.Equal(vote.EndsAt, vote.Data.AppliedAt);
            Assert.Equal(vote.EndsAt + CorporationVotingRules.CeoAuthorityDelay.Ticks, vote.Data.Election!.AuthorityAvailableAt);
            Assert.Throws<InvalidDataException>(() => voting.Activate(Candidate, id));
            Assert.Throws<InvalidDataException>(() => voting.Activate(Candidate, shareVote));
            long head = world.Services.GetRequiredService<ICorporationMembership>().Head(Candidate);
            voting.Advance();
            Assert.Equal(head, world.Services.GetRequiredService<ICorporationMembership>().Head(Candidate));
        }
        world.Services.Dispose();
        using var restarted = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        var resumed = restarted.GetRequiredService<ICorporationVoting>();
        resumed.Advance();
        Assert.Equal(Candidate, restarted.GetRequiredService<IPlayerCorporations>().Read(corporation)!.CeoId);
        clock.AdvanceWallTime(CorporationVotingRules.CeoAuthorityDelay - TimeSpan.FromTicks(1));
        resumed.Advance();
        Assert.Equal(0, restarted.GetRequiredService<IPlayerCorporations>().Identity(Candidate).Roles);
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        resumed.Advance();
        Assert.Equal(CorporationRules.CeoRoles, restarted.GetRequiredService<IPlayerCorporations>().Identity(Candidate).Roles);
        Assert.True(resumed.Read(Candidate, corporation, id).Data.HoldReleased);
        Assert.Equal(CorporationVoteType.Shares, Assert.Single(resumed.Actions(Candidate)).Data.Draft.Type);
        resumed.Activate(Candidate, shareVote);
        Assert.Equal(CorporationRules.InitialShares + 1, restarted.GetRequiredService<IPlayerCorporations>().Read(corporation)!.Shares);
        long completed = restarted.GetRequiredService<ICorporationMembership>().Head(Candidate);
        resumed.Advance();
        Assert.Equal(completed, restarted.GetRequiredService<ICorporationMembership>().Head(Candidate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedEmptyOrTiedElectionRestoresIncumbentAuthorityOnce(int ballotMode)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Prepare(world, ballotMode == 2 ? 500 : 600);
        if (ballotMode == 2)
        {
            world.Services.GetRequiredService<ICorporationShares>().Transfer(Ceo, new(corporation, true, Ceo, 500));
        }
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long id = voting.Create(Candidate, Draft());
        if (ballotMode != 0)
        {
            voting.Cast(Candidate, corporation, id, ballotMode == 2 ? 0 : 1);
        }
        if (ballotMode == 2)
        {
            voting.Cast(Ceo, corporation, id, 1);
        }
        world.Services.Dispose();
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        var resumed = reopened.GetRequiredService<ICorporationVoting>();
        resumed.Advance();
        var companies = reopened.GetRequiredService<IPlayerCorporations>();
        Assert.Equal(Ceo, companies.Read(corporation)!.CeoId);
        Assert.Equal(CorporationRules.CeoRoles, companies.Identity(Ceo).Roles);
        Assert.Null(companies.Identity(Ceo).Access!.AuthorityHoldVoteId);
        Assert.True(resumed.Read(Candidate, corporation, id).Data.HoldReleased);
        Assert.Null(resumed.Read(Candidate, corporation, id).Data.AppliedAt);
        long head = reopened.GetRequiredService<ICorporationMembership>().Head(Ceo);
        resumed.Advance();
        Assert.Equal(head, reopened.GetRequiredService<ICorporationMembership>().Head(Ceo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateDepartureAndRejoinCannotInheritTheOldMembershipElection(bool rejoin)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Prepare(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long id = voting.Create(Candidate, Draft());
        voting.Cast(Candidate, corporation, id, 0);
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        membership.Remove(Candidate, Candidate);
        if (rejoin)
        {
            // Exercise a replacement membership under the owner without restoring the suspended incumbent's invite power.
            using var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
            Eden.World.Corporations.CorporationMembershipStore.Move(action, action.Pilots.Find(Candidate)!, corporation, clock.Now);
            action.Commit();
        }
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        voting.Advance();
        Assert.Equal(Ceo, world.Services.GetRequiredService<IPlayerCorporations>().Read(corporation)!.CeoId);
        Assert.Equal(CorporationRules.CeoRoles, world.Services.GetRequiredService<IPlayerCorporations>().Identity(Ceo).Roles);
        Assert.Null(voting.Read(Candidate, corporation, id).Data.AppliedAt);
    }

    [Fact]
    public void EligibilityUsesPersonalShareThresholdInjectedSkillAndCurrentRolePreference()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Prepare(world, 49);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        Assert.False(voting.CanRunForCeo(Candidate));
        var shares = world.Services.GetRequiredService<ICorporationShares>();
        shares.Transfer(Ceo, new(corporation, true, Candidate, 1));
        Assert.True(voting.CanRunForCeo(Candidate));
        Assert.False(voting.CanRunForCeo(Ceo));
        using CorporationTestClient candidate = new(world, 1000002);
        Assert.Null(candidate.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Candidate, new(), 0, true)));
        Assert.False(voting.CanRunForCeo(Candidate));
        Assert.Throws<InvalidDataException>(() => voting.Create(Candidate, Draft()));
        Assert.Null(candidate.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Candidate, new(), 0, false)));
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            action.Remove(action.Set<PilotSkill>().Find(Candidate, CorporationRules.ManagementSkill)!);
            action.Commit();
        }
        Assert.False(voting.CanRunForCeo(Candidate));
        Assert.Throws<InvalidDataException>(() => voting.Create(Candidate, Draft()));
        Assert.Empty(voting.List(Ceo, corporation).Open);
        Assert.True(CorporationVotingRules.HasCeoElectionShares(long.MaxValue, long.MaxValue));
        Assert.False(CorporationVotingRules.HasCeoElectionShares(0, long.MaxValue));
        Assert.False(CorporationVotingRules.HasCeoElectionShares(1, 0));
        Assert.IsType<PilotCallResult.Failure>(candidate.CallService("voteManager", "CanRunForCEO", [Candidate]));
        Assert.IsType<PilotCallResult.Failure>(candidate.CallService("voteManager", "CanRunForCEO", [], new() { ["force"] = true }));
        candidate.Context.LogOff();
        Assert.Same(PilotCallResult.Unsupported, candidate.CallService("voteManager", "CanRunForCEO", []));
    }

    [Fact]
    public void WinningCandidateWhoStopsAcceptingRolesDoesNotReplaceTheIncumbent()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Prepare(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long id = voting.Create(Candidate, Draft());
        voting.Cast(Candidate, corporation, id, 0);
        using CorporationTestClient candidate = new(world, 1000002);
        Assert.Null(candidate.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Candidate, new(), 0, true)));
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        voting.Advance();
        Assert.Equal(Ceo, world.Services.GetRequiredService<IPlayerCorporations>().Read(corporation)!.CeoId);
        Assert.Equal(CorporationRules.CeoRoles, world.Services.GetRequiredService<IPlayerCorporations>().Identity(Ceo).Roles);
        Assert.Null(voting.Read(Candidate, corporation, id).Data.AppliedAt);
    }

    [Fact]
    public void ForgedCandidateAndDuplicateElectionDoNotChangeStateAndResidentAdvancementUsesNoSql()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Prepare(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        Assert.Throws<InvalidDataException>(() => voting.Create(Candidate, Draft() with { Parameter = Ceo }));
        Assert.Throws<InvalidDataException>(() => voting.Create(Candidate, Draft() with { Parameter1 = 1 }));
        Assert.Throws<InvalidDataException>(() => voting.Create(Ceo, Draft() with { Parameter = Ceo }));
        Assert.Empty(voting.List(Ceo, corporation).Open);
        Assert.Equal(CorporationRules.CeoRoles, world.Services.GetRequiredService<IPlayerCorporations>().Identity(Ceo).Roles);
        long id;
        using (var measured = SessionPerformance.Begin("resident CEO election", _ => { }, force: true)!)
        {
            id = voting.Create(Candidate, Draft());
            voting.Cast(Candidate, corporation, id, 0);
            clock.AdvanceWallTime(TimeSpan.FromDays(10));
            voting.Advance();
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
        }
        Assert.Equal(Candidate, world.Services.GetRequiredService<IPlayerCorporations>().Read(corporation)!.CeoId);
        Assert.True(voting.Read(Candidate, corporation, id).Data.HoldReleased);
        Assert.Throws<InvalidDataException>(() => voting.Create(Candidate, Draft()));
    }

    private static int Prepare(TestWorld world, long shares = 600)
    {
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new PilotSkill { PilotId = Candidate, TypeId = CorporationRules.ManagementSkill });
            action.Commit();
        }
        world.Services.GetRequiredService<ICorporationShares>().Transfer(Ceo, new(corporation, true, Candidate, shares));
        return corporation;
    }

    private static CorporationVoteDraft Draft() => new("Run for CEO", "", CorporationVoteType.Ceo, 1, ["Yes", "No"], Candidate);

    private static object?[] Options(long candidate) =>
        [
            new object?[]
            {
                "UI/SomeoneForCEO",
                new Dictionary<string, object?> { ["char"] = candidate },
            },
            new object?[] { "UI/DontChangeCEO" },
            candidate,
            null,
            null,
        ];

    private static object? Reply(CorporationTestClient client, string method, object?[] args) =>
        BlueMarshal.Decode(
            BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService("voteManager", method, args)).Reply)
        );
}
