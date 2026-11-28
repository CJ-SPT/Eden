using Eden.Data.World.Alliances;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Alliances;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class AllianceMembershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicAllianceObserversReceiveSummaryChangesWithoutPrivateEvents(bool playerCorporation)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        long observer = AdditionalPilot(world, "Alliance Observer");
        if (playerCorporation)
        {
            Corporation(world, observer, "Observer Corporation", "OBS");
        }
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        int alliance = alliances.Create(Ceo, Details()).Alliance.Id;
        using CorporationTestClient watched = new(world, 1000001, observer);
        using CorporationTestClient unwatched = new(world, 1000001, observer);
        Assert.IsType<PilotCallResult.Success>(watched.CallService("allianceRegistry", "GetAlliance", [(long)alliance]));
        var before = alliances.Read(alliance)!;
        const string description = "Open to new corporations";
        alliances.Update(Ceo, description, "");
        membership.Apply(Other, alliance, "Private application");
        membership.Decide(Ceo, applicant, "Private response", AllianceApplicationStatus.Accepted);
        clock.AdvanceWallTime(AllianceMembershipRules.AdmissionDelay);
        membership.Advance();

        watched.Advance();
        watched.Advance();
        unwatched.Advance();
        var change = Assert.Single(watched.Context.Notifications.OfType<AllianceChanged>()).Change;
        Assert.Equal(before, change.Before);
        Assert.Equal(description, change.After!.Details.Description);
        Assert.Equal(2, change.After.CorporationCount);
        Assert.DoesNotContain(watched.Context.Notifications, notice => notice is AllianceMemberChanged or AllianceApplicationChanged);
        Assert.DoesNotContain(unwatched.Context.Notifications, notice => notice is AllianceChanged);
    }

    [Fact]
    public void VacantExecutorKeepsAnApplicationRecipientWithoutGovernanceAuthority()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int founder = Corporation(world);
        int second = Corporation(world, Other, "Second Corporation", "SEC");
        long thirdCeo = AdditionalPilot(world, "Alliance Candidate");
        int third = Corporation(world, thirdCeo, "Third Corporation", "TRD");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        int alliance = alliances.Create(Ceo, Details()).Alliance.Id;
        membership.Apply(Other, alliance, "Join");
        membership.Decide(Ceo, second, "Join", AllianceApplicationStatus.Accepted);
        membership.Apply(thirdCeo, alliance, "Join");
        membership.Decide(Ceo, third, "Join", AllianceApplicationStatus.Accepted);
        clock.AdvanceWallTime(AllianceMembershipRules.AdmissionDelay);
        membership.Advance();
        membership.DeclareSupport(Other, second);
        membership.DeclareSupport(thirdCeo, third);

        var suspended = alliances.Read(alliance)!;
        Assert.Null(suspended.ExecutorCorporationId);
        long applicant = world
            .Services.GetRequiredService<ICharacterCreation>()
            .Create(1000001, new("Alliance Applicant", 1, 1, 1, 64, [1], [2], 18));
        using CorporationTestClient member = new(world, 1000001, thirdCeo);
        using CorporationTestClient outsider = new(world, 1000001, applicant);
        Assert.Null(WireExecutor(AllianceDetails(member, "GetAlliance", [])));
        Assert.Null(WireExecutor(AllianceDetails(member, "GetAlliancePublicInfo", [(long)alliance])));
        Assert.Equal(founder, WireExecutor(AllianceDetails(outsider, "GetAlliance", [(long)alliance])));
        Assert.Equal(founder, WireExecutor(AllianceDetails(outsider, "GetAlliancePublicInfo", [(long)alliance])));
        Assert.Equal(founder, WireExecutor(Ranking(outsider)));
        Assert.Null(WireExecutor(Ranking(member)));
        Assert.Throws<InvalidDataException>(() => alliances.Update(Ceo, "Unauthorized", ""));

        var corporations = world.Services.GetRequiredService<ICorporationMembership>();
        var invitation = corporations.Invite(thirdCeo, applicant);
        corporations.Update(
            applicant,
            new(invitation.Id, applicant, third, "", CorporationApplicationStatus.Joined, "", invitation.AppliedAt)
        );
        outsider.Advance();
        var changed = Assert.Single(outsider.Context.Notifications.OfType<AllianceChanged>());
        var fields = BlueRead.Fields(changed.Arguments[1]);
        Assert.Equal(new object?[] { founder, null }, BlueRead.Tuple(fields["executorCorpID"], 2));

        membership.DeclareSupport(thirdCeo, second);
        Assert.Equal(second, alliances.Read(alliance)!.ExecutorCorporationId);
        alliances.Update(Other, "Authority restored", "");

        static int? WireExecutor(AllianceReply row) =>
            (int?)Assert.IsType<object?[]>(Assert.IsType<BlueConstructor>(row.ToBlue()).Arguments[1])[3];

        static AllianceReply AllianceDetails(CorporationTestClient client, string method, object?[] args) =>
            Assert.IsType<AllianceReply>(
                Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", method, args)).Reply
            );

        static AllianceReply Ranking(CorporationTestClient client) =>
            Assert.Single(
                Assert
                    .IsType<AllianceRankingsReply>(
                        Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "GetRankedAlliances", [0L])).Reply
                    )
                    .Alliances
            );
    }

    private static long AdditionalPilot(TestWorld world, string name)
    {
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        return world.Services.GetRequiredService<ICharacterCreation>().Create(1000001, new(name, 1, 1, 1, 64, [1], [2], 18));
    }

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
    public void OnlyNewOrRejectedApplicationsCanBeDeleted()
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
        Assert.Null(client.Reply("DeleteAllianceApplication", [(long)alliance]));
        Assert.Empty(membership.Applications(Other, false));
        membership.Apply(Other, alliance, "Try again");
        membership.Decide(Ceo, applicant, "Rejected", AllianceApplicationStatus.Rejected);
        Assert.Null(client.Reply("DeleteAllianceApplication", [(long)alliance]));
        Assert.Empty(membership.Applications(Other, false));
        membership.Apply(Other, alliance, "Join again");
        membership.Decide(Ceo, applicant, "Accepted", AllianceApplicationStatus.Accepted);
        var accepted = Assert.Single(membership.Applications(Other, false));
        long head = membership.Head;
        Assert.IsType<PilotCallResult.Failure>(client.Call("DeleteAllianceApplication", [(long)alliance]));
        Assert.Equal(head, membership.Head);
        Assert.Equal(accepted, Assert.Single(membership.Applications(Other, false)));
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        Assert.Equal(alliance, alliances.Affiliation(applicant));
    }

    [Fact]
    public void LeavingClearsApplicationCachesAndAllowsTheCorporationToReapply()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        int alliance = alliances.Create(Ceo, Details()).Alliance.Id;
        membership.Apply(Other, alliance, "Join");
        membership.Decide(Ceo, applicant, "Accepted", AllianceApplicationStatus.Accepted);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        using CorporationTestClient executor = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        var effective = Assert.Single(CachedApplications(member, false));
        Assert.Equal(AllianceApplicationStatus.Effective, effective.Status);
        Assert.Equal(effective, Assert.Single(CachedApplications(executor, true)));

        Assert.IsType<PilotCallResult.Success>(member.CallService("allianceRegistry", "DeleteMember", [(long)applicant]));
        member.Advance();
        executor.Advance();
        Assert.Empty(CachedApplications(member, false));
        Assert.Empty(CachedApplications(executor, true));
        foreach (var client in new[] { member, executor })
        {
            var removed = Assert.Single(client.Context.Notifications.OfType<AllianceApplicationChanged>());
            Assert.Equal(effective, removed.Change.ApplicationBefore);
            Assert.Null(removed.Change.ApplicationAfter);
            var fields = BlueRead.Fields(removed.Arguments[2]);
            Assert.Equal(new object?[] { alliance, null }, BlueRead.Tuple(fields["allianceID"], 2));
        }
        Assert.Contains(membership.History(applicant), row => row.AllianceId == alliance);

        member.Context.Notifications.Clear();
        Assert.Null(member.Reply("ApplyToJoinAlliance", [(long)alliance, "Rejoin"]));
        member.Advance();
        var added = Assert.Single(member.Context.Notifications.OfType<AllianceApplicationChanged>());
        Assert.Null(added.Change.ApplicationBefore);
        Assert.Equal(AllianceApplicationStatus.New, added.Change.ApplicationAfter!.Status);
        Assert.Equal("Rejoin", Assert.Single(CachedApplications(member, false)).Text);

        static IReadOnlyList<AllianceApplication> CachedApplications(CorporationTestClient client, bool forAlliance)
        {
            var call = forAlliance
                ? client.CallService("allianceRegistry", "GetApplications", [])
                : client.Call("GetAllianceApplications", []);
            return Assert.IsType<AllianceApplicationsReply>(Assert.IsType<PilotCallResult.Success>(call).Reply).Applications;
        }
    }

    [Fact]
    public void ExecutorVotesStayPrivateAndNullableUpdatesKeepTheMemberRow()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int founder = Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        int alliance = alliances.Create(Ceo, Details()).Alliance.Id;
        membership.Apply(Other, alliance, "Join");
        membership.Decide(Ceo, applicant, "Accepted", AllianceApplicationStatus.Accepted);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        using CorporationTestClient executor = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);

        var privateReply = Assert.IsType<AllianceMembersReply>(
            Assert.IsType<PilotCallResult.Success>(member.CallService("allianceRegistry", "GetMembers", [])).Reply
        );
        Assert.Null(privateReply.Members.Single(row => row.CorporationId == founder).ChosenExecutorId);
        Assert.Equal(founder, privateReply.Members.Single(row => row.CorporationId == applicant).ChosenExecutorId);
        var publicReply = Assert.IsType<AlliancePublicMembersReply>(
            Assert.IsType<PilotCallResult.Success>(member.CallService("allianceRegistry", "GetAllianceMembers", [(long)alliance])).Reply
        );
        Assert.All(publicReply.Members, row => Assert.Null(row.ChosenExecutorId));

        membership.DeclareSupport(Ceo, applicant);
        executor.Advance();
        member.Advance();
        var ownVote = Assert.Single(executor.Context.Notifications.OfType<AllianceMemberChanged>());
        Assert.Equal(applicant, ownVote.Change.MemberAfter!.ChosenExecutorId);
        Assert.Empty(member.Context.Notifications.OfType<AllianceMemberChanged>());

        membership.Remove(Ceo, founder);
        member.Advance();
        var ownUpdate = Assert.Single(
            member.Context.Notifications.OfType<AllianceMemberChanged>(),
            change => change.Change.CorporationId == applicant
        );
        var fields = BlueRead.Fields(BlueMarshal.Decode(BlueMarshal.Encode(ownUpdate.Arguments[2])));
        Assert.Equal(new object?[] { (long)applicant, (long)applicant }, BlueRead.Tuple(fields["corporationID"], 2));
        Assert.Equal(new object?[] { (long)founder, null }, BlueRead.Tuple(fields["chosenExecutorID"], 2));
        var departure = Assert.Single(
            member.Context.Notifications.OfType<AllianceMemberChanged>(),
            change => change.Change.CorporationId == founder
        );
        var removed = BlueRead.Fields(departure.Arguments[2]);
        Assert.Equal(new object?[] { founder, null }, BlueRead.Tuple(removed["corporationID"], 2));
        Assert.Equal(new object?[] { null, null }, BlueRead.Tuple(removed["chosenExecutorID"], 2));
    }

    [Fact]
    public void MemberVotesUseCommittedCorporationAndOnlyTheCeoCanApply()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int founder = Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        long director = world
            .Services.GetRequiredService<ICharacterCreation>()
            .Create(1000001, new("Alliance Director", 1, 1, 1, 64, [1], [2], 18));
        var corporations = world.Services.GetRequiredService<IPlayerCorporations>();
        var corporationMembers = world.Services.GetRequiredService<ICorporationMembership>();
        var roles = world.Services.GetRequiredService<ICorporationRoles>();
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        int alliance = alliances.Create(Ceo, Details()).Alliance.Id;
        Join(Other);
        CorporationRolePatch directorRoles = new((long)CorporationRole.Director, null, null, null, null, null, null, null);
        roles.UpdateMembers(Other, [new(director, null, null, null, directorRoles, null, null)]);
        Assert.True(corporations.Identity(director).Access!.Roles.Has(CorporationRole.Director));
        using CorporationTestClient client = new(world, 1000001, director);
        Assert.IsType<PilotCallResult.Failure>(client.Call("ApplyToJoinAlliance", [(long)alliance, "Join"]));
        Assert.Empty(membership.Applications(Other, false));

        roles.UpdateMembers(Other, [new(director, null, null, null, directorRoles with { Roles = 0 }, null, null)]);
        membership.Apply(Other, alliance, "Join");
        membership.Decide(Ceo, applicant, "Accepted", AllianceApplicationStatus.Accepted);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        client.Advance();
        Join(Ceo);
        Assert.Equal(founder, corporations.Identity(director).Id);
        Assert.Equal(applicant, client.Context.Pilot.CorpId);
        Assert.Equal(alliance, client.Context.Pilot.Identity.AllianceId);
        var result = client.CallService(
            "allianceRegistry",
            "MachoBindObject",
            [new object?[] { (long)alliance, 1L }, new object?[] { "GetMembers", Array.Empty<object?>() }]
        );
        var bound = Assert.IsType<BoundPilotReply>(Assert.IsType<PilotCallResult.Success>(result).Reply);
        var members = Assert.IsType<AllianceMembersReply>(bound.Reply).Members;
        Assert.Equal(founder, members.Single(row => row.CorporationId == founder).ChosenExecutorId);
        Assert.Null(members.Single(row => row.CorporationId == applicant).ChosenExecutorId);

        void Join(long recruiter)
        {
            var invitation = corporationMembers.Invite(recruiter, director);
            corporationMembers.Update(
                director,
                new(invitation.Id, director, invitation.CorporationId, "", CorporationApplicationStatus.Joined, "", invitation.AppliedAt)
            );
        }
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
