using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationVotingTests
{
    [Fact]
    public void NativeForeignVoteListAcceptsTheAllStatusesArgument()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        PlayerCorporationTests.Prepare(world);
        int corporation = world
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(90000001, PlayerCorporationTests.Details())
            .Corporation.Id;
        world.Services.GetRequiredService<ICorporationShares>().Transfer(90000001, new(corporation, true, 90000002, 400));
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        var draft = new CorporationVoteDraft("Destination", "", CorporationVoteType.General, 1, ["A", "B"]);
        long closed = voting.Create(90000001, draft);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        long open = voting.Create(90000001, draft);
        using CorporationTestClient shareholder = new(world, 1000002);
        Assert.NotEqual(corporation, shareholder.Context.Pilot.CorpId);
        var result = Assert.IsType<PilotCallResult.Success>(
            shareholder.CallService("voteManager", "GetVoteCasesByCorporation", [(long)corporation, 0L])
        );
        var reply = Assert.IsType<CorporationVoteCasesReply>(result.Reply);
        Assert.Equal(open, Assert.Single(reply.Votes.Open).Id);
        Assert.Equal(closed, Assert.Single(reply.Votes.Closed).Id);
        Assert.NotEmpty(BlueMarshal.Encode(reply));
    }

    [Fact]
    public void NativeGetVotesResolvesTheCorporationFromTheCaseAndReturnsOnlyTheCallersBallots()
    {
        using TestWorld world = new(liveWorld: true);
        PlayerCorporationTests.Prepare(world);
        int corporation = world
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(90000001, PlayerCorporationTests.Details())
            .Corporation.Id;
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        var shares = world.Services.GetRequiredService<ICorporationShares>();
        var draft = new CorporationVoteDraft("Destination", "", CorporationVoteType.General, 1, ["A", "B"]);
        long restrictedCase = voting.Create(90000001, draft);
        using CorporationTestClient shareholder = new(world, 1000002);
        Assert.IsType<PilotCallResult.Failure>(shareholder.CallService("voteManager", "GetVotes", [restrictedCase]));
        shares.Transfer(90000001, new(corporation, true, 90000002, 400));
        long vote = voting.Create(90000001, draft);
        Assert.Empty(Assert.IsType<List<object?>>(Reply(shareholder, "GetVotes", [vote])));
        voting.Cast(90000001, corporation, vote, 0);
        voting.Cast(90000002, corporation, vote, 1);
        var ballots = Assert.IsType<List<object?>>(Reply(shareholder, "GetVotes", [vote]));
        var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(ballots)).State);
        Assert.Equal(90000002L, fields["characterID"]);
        Assert.Equal(1L, fields["optionID"]);
        Assert.Equal(400L, fields["voteWeight"]);
        Assert.Equal(vote, fields["voteCaseID"]);
    }

    [Fact]
    public void NativeShareVotePublishesTalliesAndIssuesSharesOnlyOnceAfterSanction()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            Assert.Equal(true, Reply(ceo, "CanViewVotes", [(long)corporation]));
            Assert.Equal(false, Reply(member, "CanViewVotes", [(long)corporation]));
            object?[] options =
            [
                new object?[]
                {
                    "UI/CreateShares",
                    new Dictionary<string, object?> { ["shares"] = 500L },
                },
                new object?[] { "UI/DoNotCreateShares" },
                500L,
                null,
                null,
            ];
            long id = Assert.IsType<long>(Reply(ceo, "InsertVoteCase", ["New shares", "Finance exploration", 2L, options, 1L]));
            var cases = Assert.IsType<CorporationVoteCasesReply>(
                Assert
                    .IsType<PilotCallResult.Success>(ceo.CallService("voteManager", "GetVoteCasesByCorporation", [(long)corporation]))
                    .Reply
            );
            Assert.Equal(id, Assert.Single(cases.Votes.Open).Id);
            Assert.NotEmpty(BlueMarshal.Encode(cases));
            Assert.Null(Reply(ceo, "InsertVote", [(long)corporation, id, 0L]));
            Assert.IsType<PilotCallResult.Failure>(ceo.CallService("voteManager", "InsertVote", [(long)corporation, id, 1L]));
            var optionRows = Assert.IsType<List<object?>>(Reply(ceo, "GetVoteCaseOptions", [(long)corporation, id]));
            Assert.Equal(1000L, BlueRead.Fields(Assert.IsType<BlueInstance>(optionRows[0]).State)["votesFor"]);
            Assert.Single(Assert.IsType<List<object?>>(Reply(ceo, "GetVotes", [(long)corporation, id])));
            Assert.IsType<PilotCallResult.Failure>(ceo.CallService("voteManager", "ActivateSanctionedAction", [id]));
            var voting = world.Services.GetRequiredService<ICorporationVoting>();
            clock.AdvanceWallTime(TimeSpan.FromDays(1));
            voting.Advance();
            ceo.Advance();
            int eventCount = ceo.Context.Notifications.Count;
            voting.Advance();
            ceo.Advance();
            Assert.Equal(eventCount, ceo.Context.Notifications.Count);
            Assert.Contains(ceo.Context.Notifications, notification => notification.Name == "OnVoteCaseChanged");
            Assert.Contains(ceo.Context.Notifications, notification => notification.Name == "OnSanctionedActionChanged");
            Assert.Single(Assert.IsType<Dictionary<long, object?>>(Reply(ceo, "GetCorpSanctionedActions", [])));
            Assert.IsType<PilotCallResult.Failure>(member.CallService("voteManager", "ActivateSanctionedAction", [id]));
            Assert.Null(Reply(ceo, "ActivateSanctionedAction", [id]));
            Assert.Equal(1500, world.Services.GetRequiredService<IPlayerCorporations>().Read(corporation)!.Shares);
            Assert.Equal(1500, Assert.Single(world.Services.GetRequiredService<ICorporationShares>().Holdings(90000001, true)).Shares);
            Assert.IsType<PilotCallResult.Failure>(ceo.CallService("voteManager", "ActivateSanctionedAction", [id]));
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        Assert.Equal(1500, reopened.GetRequiredService<IPlayerCorporations>().Read(corporation)!.Shares);
        Assert.NotNull(Assert.Single(reopened.GetRequiredService<ICorporationVoting>().Actions(90000001)).Data.AppliedAt);
    }

    [Fact]
    public void PollWeightsAreFrozenAndTransfersCannotVoteTheSameSharesTwice()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var shares = world.Services.GetRequiredService<ICorporationShares>();
        shares.Transfer(90000001, new(corporation, true, 90000002, 600));
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long id = voting.Create(90000001, new("Destination", "", CorporationVoteType.General, 1, ["Lowsec", "Highsec", "Wormholes"]));
        voting.Cast(90000002, corporation, id, 2);
        shares.Transfer(90000002, new(corporation, false, 90000001, 600));
        voting.Cast(90000001, corporation, id, 0);
        var vote = voting.Read(90000001, corporation, id);
        Assert.Equal(1000, vote.Data.Ballots.Sum(ballot => ballot.Weight));
        Assert.Equal(2, CorporationVotingRules.Winner(vote.Data));
        Assert.Throws<InvalidDataException>(() => voting.Cast(90000001, corporation, id, 0));
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        voting.Advance();
        Assert.Empty(voting.List(90000001, corporation).Open);
        Assert.Empty(voting.Actions(90000001));
        Assert.Throws<InvalidDataException>(() => voting.Cast(90000001, corporation, id, 1));
    }

    [Fact]
    public void TiesAndEmptyPollsHaveNoWinnerAndExpiredSanctionsCannotIssueShares()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        var draft = new CorporationVoteDraft("Issue", "", CorporationVoteType.Shares, 1, ["Yes", "No"], 10);
        long id = voting.Create(90000001, draft);
        Assert.Null(CorporationVotingRules.Winner(voting.Read(90000001, corporation, id).Data));
        voting.Cast(90000001, corporation, id, 0);
        clock.AdvanceWallTime(TimeSpan.FromDays(8));
        Assert.Throws<InvalidDataException>(() => voting.Activate(90000001, id));
        Assert.Equal(1000, world.Services.GetRequiredService<IPlayerCorporations>().Read(corporation)!.Shares);
        CorporationVoteData tied = new(1, draft, [], [new(1, 1, 0, long.MaxValue, 1), new(2, 2, 1, long.MaxValue, 1)]);
        Assert.Null(CorporationVotingRules.Winner(tied));
        Assert.Throws<InvalidDataException>(() => voting.Create(90000002, draft));
        Assert.Throws<InvalidDataException>(() => voting.Create(90000001, draft with { Days = 31 }));
        Assert.Throws<InvalidDataException>(() => voting.Read(90000002, corporation, id));
    }

    [Fact]
    public void OverflowAndForgedCorporateIdentifiersCannotApplyOrModifyAVote()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var voting = world.Services.GetRequiredService<ICorporationVoting>();
        long id = voting.Create(90000001, new("Excess supply", "", CorporationVoteType.Shares, 1, ["Yes", "No"], long.MaxValue));
        voting.Cast(90000001, corporation, id, 0);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        voting.Advance();
        Assert.Throws<InvalidDataException>(() => voting.Activate(90000001, id));
        Assert.Null(voting.Read(90000001, corporation, id).Data.AppliedAt);
        Assert.Throws<InvalidDataException>(() => voting.Read(90000001, corporation + 1, id));
        Assert.Throws<InvalidDataException>(() => voting.Cast(90000001, corporation + 1, id, 0));
        using CorporationTestClient ceo = new(world, 1000001);
        Assert.IsType<PilotCallResult.Failure>(
            ceo.CallService(
                "voteManager",
                "InsertVoteCase",
                ["Forged", "", 4L, new List<object?> { "A", "B" }, 1L],
                new() { ["corporationID"] = corporation }
            )
        );
        using WorldDbContext saved = new(world.Path);
        Assert.Equal(1000, saved.Set<SavedCorporationShareIssue>().Find(corporation)!.Shares);
        Assert.Single(saved.Set<SavedCorporationVote>());
    }

    [Fact]
    public void MigrationAndRestartPreserveVoteDeadlinesWithoutRepeatingSettlement()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw("DROP TABLE WorldCorporationVote; PRAGMA user_version=163;");
        }
        WorldInitialization.Initialize(new(world.Path));
        long id;
        using (var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true))
        {
            var voting = reopened.GetRequiredService<ICorporationVoting>();
            id = voting.Create(90000001, new("Restart", "", CorporationVoteType.General, 1, ["A", "B"]));
            voting.Cast(90000001, corporation, id, 0);
        }
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        using var restarted = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        var resumed = restarted.GetRequiredService<ICorporationVoting>();
        resumed.Advance();
        Assert.True(resumed.Read(90000001, corporation, id).Data.Settled);
        long head = restarted.GetRequiredService<ICorporationMembership>().Head(90000001);
        resumed.Advance();
        Assert.Equal(head, restarted.GetRequiredService<ICorporationMembership>().Head(90000001));
    }

    private static object? Reply(CorporationTestClient client, string method, object?[] args) =>
        BlueMarshal.Decode(
            BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService("voteManager", method, args)).Reply)
        );
}
