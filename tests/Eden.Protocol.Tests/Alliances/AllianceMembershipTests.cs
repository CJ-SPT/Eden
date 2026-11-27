using Eden.Data.World.Alliances;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Alliances;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class AllianceMembershipTests
{
    [Fact]
    public async Task NativeCreationPublishesAllianceSessionChangeBeforeReply()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Corporation(world);
        using var scope = world.Services.CreateScope();
        var details = Details();
        object?[] allianceBinding = [(long)AllianceRules.FirstAllianceId, 1L];
        const string corporationRegistry = "N=10001:42-2";
        const string allianceRegistry = "N=10001:42-3";
        Queue<object?> input = new([
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [Ceo]),
            PilotServicesTests.Request("corpRegistry", "MachoBindObject", [(long)corporation, null]),
            PilotServicesTests.Request(
                null,
                "CreateAlliance",
                [details.Name, details.Ticker, details.Description, details.Url],
                corporationRegistry
            ),
            PilotServicesTests.Request("allianceRegistry", "MachoResolveObject", [allianceBinding]),
            PilotServicesTests.Request("allianceRegistry", "MachoBindObject", [allianceBinding, null]),
            PilotServicesTests.Request(null, "GetAlliance", [], allianceRegistry),
            PilotServicesTests.Request(
                "allianceRegistry",
                "MachoBindObject",
                [
                    allianceBinding,
                    new object?[]
                    {
                        "GetMembers",
                        Array.Empty<object?>(),
                        new Dictionary<string, object?> { ["machoVersion"] = 1L },
                    },
                ]
            ),
            PilotServicesTests.Request(null, "GetMembers", [], allianceRegistry),
        ]);
        List<BlueInstance> packets = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            scope
                .ServiceProvider.GetRequiredService<IPilotSession>()
                .RunAsync(
                    () => input.TryDequeue(out var packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
                    packet =>
                    {
                        packets.Add(Assert.IsType<BlueInstance>(packet));
                        return Task.CompletedTask;
                    },
                    _ => { },
                    1000001,
                    42,
                    SignedSettings.Empty,
                    CancellationToken.None
                )
        );
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var alliance = Assert.Single(alliances.List());
        int id = alliance.Id;
        var errors = packets
            .Where(p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal))
            .Select(p => BlueRead.Tuple(BlueRead.Tuple(p.State, 14)[4], 3)[2]);
        Assert.Empty(errors);
        var replies = packets.Where(p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(8, replies.Length);
        Assert.Equal((long)id, BlueMarshal.Decode(ReplyBytes(replies[2])));
        int changeIndex = packets.FindIndex(p =>
        {
            if (!p.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal))
            {
                return false;
            }
            var change = BlueRead.Fields(BlueRead.Tuple(BlueRead.Tuple(BlueRead.Tuple(p.State, 14)[4], 3)[1], 2)[1]);
            return change.TryGetValue("allianceid", out var value) && Equals(BlueRead.Tuple(value, 2)[1], id);
        });
        Assert.InRange(changeIndex, 0, packets.IndexOf(replies[2]) - 1);
        Assert.Equal((long)PilotLogin.NodeId, BlueMarshal.Decode(ReplyBytes(replies[3])));
        BlueObjectReference reference = new(allianceRegistry, 3);
        var none = Assert.IsType<PilotCallResult.Success>(PilotCallResult.None).Reply;
        var members = new AllianceMembersReply(alliances.Members(id));
        var wars = world.Services.GetRequiredService<ICorporationWars>();
        Assert.Equal(BlueMarshal.Encode(new BoundPilotReply(reference, none)), ReplyBytes(replies[4]));
        Assert.Equal(BlueMarshal.Encode(new AllianceReply(alliance, wars.IsEligible(id))), ReplyBytes(replies[5]));
        Assert.Equal(BlueMarshal.Encode(new BoundPilotReply(reference, members)), ReplyBytes(replies[6]));
        Assert.Equal(BlueMarshal.Encode(members), ReplyBytes(replies[7]));

        using CorporationTestClient founder = new(world, 1000001);
        object?[] wrongAlliance = [(long)id + 1, 1L];
        Assert.IsType<PilotCallResult.Failure>(founder.CallService("allianceRegistry", "MachoResolveObject", [wrongAlliance]));
        Assert.IsType<PilotCallResult.Failure>(
            founder.CallService(
                "allianceRegistry",
                "MachoBindObject",
                [wrongAlliance, new object?[] { "GetMembers", Array.Empty<object?>() }]
            )
        );

        static byte[] ReplyBytes(BlueInstance packet) => BlueRead.Bytes(BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1)[0]);
    }

    [Fact]
    public async Task AcceptedApplicationWaitsExactlyOneDayAndResumesAfterReopen()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int founder = Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        int alliance = alliances.Create(Ceo, Details()).Alliance.Id;
        using (CorporationTestClient executor = new(world, 1000001))
        using (CorporationTestClient applying = new(world, 1000002))
        {
            Assert.Null(applying.Reply("ApplyToJoinAlliance", [(long)alliance, "Please admit us"]));
            var pending = Assert.Single(membership.Applications(Ceo, true));
            Assert.Equal(AllianceApplicationStatus.New, pending.Status);
            Assert.IsType<PilotCallResult.Failure>(applying.CallService("allianceRegistry", "GetApplications", []));
            Assert.IsType<PilotCallResult.Success>(
                executor.CallService(
                    "allianceRegistry",
                    "UpdateApplication",
                    [(long)applicant, pending.Text, (long)AllianceApplicationStatus.Accepted]
                )
            );
            long deadline = Assert.Single(membership.Applications(Other, false)).EffectiveAt!.Value;
            clock.AdvanceWallTime(TimeSpan.FromHours(23));
            membership.Decide(Ceo, applicant, pending.Text, AllianceApplicationStatus.Accepted);
            Assert.Equal(deadline, Assert.Single(membership.Applications(Other, false)).EffectiveAt);
            membership.Advance();
            Assert.Null(alliances.Affiliation(applicant));
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var resumed = reopened.GetRequiredService<IAllianceMembership>();
        clock.AdvanceWallTime(TimeSpan.FromHours(1) - TimeSpan.FromTicks(1));
        resumed.Advance();
        Assert.Null(reopened.GetRequiredService<IAlliances>().Affiliation(applicant));
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        resumed.Advance();
        long head = resumed.Head;
        resumed.Advance();
        Assert.Equal(head, resumed.Head);
        var members = reopened.GetRequiredService<IAlliances>().Members(alliance);
        Assert.Equal(new[] { founder, applicant }, members.Select(m => m.CorporationId).Order().ToArray());
        Assert.Equal(AllianceApplicationStatus.Effective, Assert.Single(resumed.Applications(Other, false)).Status);
        Assert.Equal(alliance, resumed.History(applicant)[0].AllianceId);
        Assert.Null(resumed.History(applicant)[^1].AllianceId);
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public void SessionsRefreshAffiliationReleaseBindingsAndReceiveSingleMembershipDelta()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        int alliance = alliances.Create(Ceo, Details()).Alliance.Id;
        using CorporationTestClient client = new(world, 1000002);
        membership.Apply(Other, alliance, "Join");
        membership.Decide(Ceo, applicant, "Join", AllianceApplicationStatus.Accepted);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        client.Advance();
        Assert.Equal(alliance, client.Context.Pilot.Identity.AllianceId);
        var addition = Assert.Single(client.Context.Notifications.OfType<AllianceMemberChanged>());
        Assert.Null(addition.Change.MemberBefore);
        Assert.Equal(applicant, addition.Change.MemberAfter!.CorporationId);
        Assert.NotEmpty(BlueMarshal.Encode(addition.Arguments));
        var reference = client.Context.Reference("allianceRegistry");
        client.Context.Notifications.Clear();
        membership.Remove(Other, applicant);
        client.Advance();
        Assert.Null(client.Context.Pilot.Identity.AllianceId);
        Assert.Null(client.Context.ResolveReference(reference.Id));
        Assert.Null(Assert.Single(client.Context.Notifications.OfType<AllianceMemberChanged>()).Change.MemberAfter);
        client.Advance();
        Assert.Single(client.Context.Notifications.OfType<AllianceMemberChanged>());
        Assert.Null(membership.History(applicant)[0].AllianceId);
    }

    [Fact]
    public void ReplacementWithdrawalAndStaleDeadlineCannotAdmitAnApplicant()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        int alliance = alliances.Create(Ceo, Details()).Alliance.Id;
        membership.Apply(Other, alliance, "Join");
        membership.Decide(Ceo, applicant, "Join", AllianceApplicationStatus.Accepted);
        membership.Withdraw(Other, alliance);
        clock.AdvanceWallTime(TimeSpan.FromDays(2));
        membership.Advance();
        Assert.Null(alliances.Affiliation(applicant));
        Assert.Empty(membership.Applications(Other, false));
        membership.Apply(Other, alliance, "Try again");
        membership.Decide(Ceo, applicant, "Rejected", AllianceApplicationStatus.Rejected);
        Assert.Null(alliances.Affiliation(applicant));
        Assert.Equal(AllianceApplicationStatus.Rejected, Assert.Single(membership.Applications(Other, false)).Status);
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(3, 1, 2)]
    [InlineData(4, 1, 2)]
    public void ExecutorUsesCorporationSupportAndRetainsAnIncumbentTie(int count, int incumbent, int supporter)
    {
        var members = Enumerable
            .Range(1, count)
            .Select(id => new AllianceMember(id, 1, 0, id <= (count + 1) / 2 ? supporter : incumbent))
            .ToArray();
        Assert.Equal(supporter == incumbent || count == 4 ? incumbent : supporter, AllianceMembershipRules.Executor(members, incumbent));
        Assert.Null(AllianceMembershipRules.Executor(members.Select(m => m with { ChosenExecutorId = 999 }).ToArray(), incumbent));
    }

    [Fact]
    public void SupportTransferRevokesOldExecutorAndClosureRemovesMembershipAtomically()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int founder = Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        int alliance = alliances.Create(Ceo, Details()).Alliance.Id;
        membership.Apply(Other, alliance, "Join");
        membership.Decide(Ceo, applicant, "Join", AllianceApplicationStatus.Accepted);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        membership.DeclareSupport(Other, applicant);
        Assert.Equal(founder, alliances.Read(alliance)!.ExecutorCorporationId);
        membership.DeclareSupport(Ceo, applicant);
        Assert.Equal(applicant, alliances.Read(alliance)!.ExecutorCorporationId);
        Assert.Throws<InvalidDataException>(() => alliances.Update(Ceo, "Old executor", ""));
        Assert.Throws<InvalidDataException>(() => membership.Remove(Ceo, applicant));
        alliances.Update(Other, "New executor", "");
        membership.Remove(Other, founder);
        Assert.Null(alliances.Affiliation(founder));
        Assert.Single(alliances.Members(alliance));
        world.Services.GetRequiredService<ICorporationLeadership>().Close(Other);
        Assert.Null(alliances.Affiliation(applicant));
        Assert.Empty(alliances.Members(alliance));
        Assert.Null(alliances.Read(alliance)!.ExecutorCorporationId);
    }

    [Fact]
    public void ApplyingElsewhereWithdrawsTheOldAcceptedApplication()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int founder = Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        int first = alliances.Create(Ceo, Details()).Alliance.Id;
        membership.Apply(Other, first, "Original");
        membership.Decide(Ceo, applicant, "Original", AllianceApplicationStatus.Accepted);
        membership.Remove(Ceo, founder);
        int second = alliances.Create(Ceo, Details("Second Alliance", "SEC")).Alliance.Id;
        membership.Apply(Other, second, "Replacement");
        Assert.Equal(second, Assert.Single(membership.Applications(Other, false)).AllianceId);
        clock.AdvanceWallTime(TimeSpan.FromDays(2));
        membership.Advance();
        Assert.Null(alliances.Affiliation(applicant));
        Assert.Empty(alliances.Members(first));
    }

    [Fact]
    public void WarmedMembershipAndIdleMaintenanceUseNoSqlConnectionsOrFittingWork()
    {
        using TestWorld world = new(liveWorld: true);
        Corporation(world);
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        int id = alliances.Create(Ceo, Details()).Alliance.Id;
        using CorporationTestClient client = new(world, 1000001);
        client.Advance();
        using var metrics = SessionPerformance.Begin("alliance idle membership", _ => { }, force: true)!;
        for (int cycle = 0; cycle < 10; cycle++)
        {
            membership.Advance();
            client.Advance();
            Assert.Single(alliances.Members(id));
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
