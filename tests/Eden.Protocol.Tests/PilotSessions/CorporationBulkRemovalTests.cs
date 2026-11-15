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
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationBulkRemovalTests
{
    private const long Ceo = 90000001;
    private const long Member = 90000002;
    private const int NpcCorporation = 1000044;

    [Fact]
    public void NativeBatchReturnsMixedListsAndPersistsMembershipAndOrderedNotifications()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        PrepareAdditionalPilots(world);
        long other = CreatePilot(world, "Other Member");
        Join(world, other);
        long outsider = CreatePilot(world, "Outside Pilot");
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var companies = world.Services.GetRequiredService<IPlayerCorporations>();
        var before = world.Store.FindPilot(1000002)!;
        world.Services.GetRequiredService<ICorporationShares>().Transfer(Ceo, new(corporation, true, Member, 10));
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            ceo.Context.Notifications.Clear();
            member.Context.Notifications.Clear();
            var reply = BlueRead.Fields(ceo.Reply("KickOutMembers", [new List<object?> { Member, 42L, Member, outsider, other }]));
            Assert.Equal(new object?[] { Member, other }, Assert.IsType<List<object?>>(reply["kicked"]));
            Assert.Equal(new object?[] { 42L, outsider }, Assert.IsType<List<object?>>(reply["notKicked"]));
            Assert.Equal(NpcCorporation, companies.Identity(Member).Id);
            Assert.Equal(NpcCorporation, companies.Identity(other).Id);
            Assert.Equal(NpcCorporation, companies.Identity(outsider).Id);
            Assert.Single(companies.Members(Ceo));
            Assert.Equal(new[] { 2, 1 }, ceo.Context.Notifications.OfType<CorporationCountChanged>().Select(change => change.After));
            member.Advance();
            Assert.Equal(NpcCorporation, member.Context.Pilot.Identity.CorporationId);
            Assert.Equal(
                new[] { Member, other },
                member.Context.Notifications.OfType<CorporationMemberChanged>().Select(change => change.Change.PilotId)
            );
            ceo.Context.Notifications.Clear();
            member.Context.Notifications.Clear();
            ceo.Advance();
            member.Advance();
            Assert.Empty(ceo.Context.Notifications);
            Assert.Empty(member.Context.Notifications);
            long head = membership.Head(Ceo);
            var repeat = membership.RemoveMany(Ceo, [Member, other]);
            Assert.Empty(repeat.Kicked);
            Assert.Equal(new[] { Member, other }, repeat.NotKicked);
            Assert.Equal(head, membership.Head(Ceo));
        }
        var after = world.Store.FindPilot(1000002)!;
        Assert.Equal((before.ShipId, before.StationId, before.Position), (after.ShipId, after.StationId, after.Position));
        Assert.Equal(10, Assert.Single(world.Services.GetRequiredService<ICorporationShares>().Holdings(Member, false)).Shares);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        var persisted = reopened.GetRequiredService<IPlayerCorporations>();
        Assert.Equal(NpcCorporation, persisted.Identity(Member).Id);
        Assert.Equal(NpcCorporation, persisted.Identity(other).Id);
        Assert.Single(persisted.Members(Ceo));
        Assert.Equal(new[] { NpcCorporation, corporation, NpcCorporation }, persisted.Employment(Member).Select(job => job.CorporationId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BatchPreservesRoleTitleCooldownAndShareholderProtection(int protection)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        PrepareAdditionalPilots(world);
        long eligible = CreatePilot(world, "Eligible Member");
        Join(world, eligible);
        using CorporationTestClient ceo = new(world, 1000001);
        switch (protection)
        {
            case 0:
                Assert.Null(
                    ceo.Reply(
                        "UpdateMember",
                        CorporationRoleTests.MemberArguments(Member, new(GrantableRoles: (long)CorporationRole.Accountant))
                    )
                );
                break;
            case 1:
                world
                    .Services.GetRequiredService<ICorporationRoles>()
                    .UpdateTitles(Ceo, [new(1, "Accountant", new((long)CorporationRole.Accountant))]);
                Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new(), 1)));
                break;
            case 2:
                Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new((long)CorporationRole.Accountant))));
                Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new())));
                clock.AdvanceWallTime(CorporationRoleRules.RemovalDelay - TimeSpan.FromTicks(1));
                break;
            default:
                world.Services.GetRequiredService<ICorporationShares>().Transfer(Ceo, new(corporation, true, Member, 600));
                break;
        }
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var result = membership.RemoveMany(Ceo, [eligible, Member]);
        Assert.Equal(new[] { eligible }, result.Kicked);
        Assert.Equal(new[] { Member }, result.NotKicked);
        Assert.Equal(corporation, world.Services.GetRequiredService<IPlayerCorporations>().Identity(Member).Id);
        if (protection == 2)
        {
            clock.AdvanceWallTime(TimeSpan.FromTicks(1));
            Assert.Equal(new[] { Member }, membership.RemoveMany(Ceo, [Member]).Kicked);
        }
    }

    [Fact]
    public void BatchRequiresCurrentDirectorAndRejectsSelfBeforeChangingAnyMember()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        PrepareAdditionalPilots(world);
        long eligible = CreatePilot(world, "Director Target");
        Join(world, eligible);
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        Assert.IsType<PilotCallResult.Failure>(member.Call("KickOutMembers", [new List<object?> { Ceo }]));
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new((long)CorporationRole.PersonnelManager))));
        Assert.IsType<PilotCallResult.Failure>(member.Call("KickOutMembers", [new List<object?> { Ceo }]));
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new((long)CorporationRole.Director))));
        var result = membership.RemoveMany(Member, [Ceo, eligible]);
        Assert.Equal(new[] { eligible }, result.Kicked);
        Assert.Equal(new[] { Ceo }, result.NotKicked);
        Assert.Throws<InvalidDataException>(() => membership.RemoveMany(Ceo, [Member, Ceo]));
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new())));
        Assert.IsType<PilotCallResult.Failure>(member.Call("KickOutMembers", [new List<object?> { Ceo }]));
        Assert.Equal(2, world.Services.GetRequiredService<IPlayerCorporations>().Members(Ceo).Count);
        Assert.Equal(corporation, world.Services.GetRequiredService<IPlayerCorporations>().Identity(Member).Id);
    }

    [Fact]
    public void NativeBatchRejectsMalformedRequestsAndSelectionBypassesWithoutChanges()
    {
        using TestWorld world = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        long head = membership.Head(Ceo);
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [Member],
                [new object?[] { Member }],
                [new List<object?>()],
                [new List<object?> { Member, "42" }],
                [new List<object?> { Member, 0L }],
                [new List<object?> { Member, -1L }],
                [new List<object?> { Member, null }],
                [Enumerable.Repeat<object?>(Member, CorporationMembershipRules.MaximumMemberRemoval + 1).ToList()],
                [new List<object?> { Member }, true],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(ceo.Call("KickOutMembers", args));
        }
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("KickOutMembers", [new List<object?> { Member }], new() { ["force"] = true }));
        Assert.Equal(head, membership.Head(Ceo));
        Assert.Equal(2, world.Services.GetRequiredService<IPlayerCorporations>().Members(Ceo).Count);
        ceo.Context.LogOff();
        Assert.Same(PilotCallResult.Unsupported, ceo.Call("KickOutMembers", [new List<object?> { Member }]));
    }

    [Fact]
    public void BulkRemovalReadsResidentStateWithoutSqlOrFittingWork()
    {
        using TestWorld world = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        _ = membership.CanRemove(Ceo, Member);
        using var measured = SessionPerformance.Begin("resident bulk removal", _ => { }, force: true)!;
        Assert.Equal(new[] { Member }, membership.RemoveMany(Ceo, [Member]).Kicked);
        Assert.Equal(0, measured.Commands);
        Assert.Equal(0, measured.Connections);
        Assert.Equal(0, measured.FittingCalculations);
    }

    [Fact]
    public void FailedBatchRollsBackEarlierDeparturesEmploymentAndNotifications()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        PrepareAdditionalPilots(world);
        long broken = CreatePilot(world, "Broken History");
        Join(world, broken);
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            action.RemoveRange(
                action.Set<SavedCorporationEmployment>().Where(row => row.PilotId == broken && row.CorporationId == NpcCorporation)
            );
            action.Commit();
        }
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var companies = world.Services.GetRequiredService<IPlayerCorporations>();
        long head = membership.Head(Ceo);
        var employment = companies.Employment(Member).ToArray();
        Assert.Throws<InvalidDataException>(() => membership.RemoveMany(Ceo, [Member, broken]));
        Assert.Equal(corporation, companies.Identity(Member).Id);
        Assert.Equal(corporation, companies.Identity(broken).Id);
        Assert.Equal(employment, companies.Employment(Member));
        Assert.Equal(head, membership.Head(Ceo));
        Assert.Equal(new[] { Member }, membership.RemoveMany(Ceo, [Member]).Kicked);
    }

    [Fact]
    public void TypedTargetValidationBoundsTheRequestAndRejectsSelfAndInvalidIds()
    {
        Assert.Throws<InvalidDataException>(() => CorporationMembershipRules.CheckRemovalTargets(Ceo, []));
        Assert.Throws<InvalidDataException>(() => CorporationMembershipRules.CheckRemovalTargets(Ceo, [Member, Ceo]));
        Assert.Throws<InvalidDataException>(() => CorporationMembershipRules.CheckRemovalTargets(Ceo, [Member, 0]));
        Assert.Throws<InvalidDataException>(() => CorporationMembershipRules.CheckRemovalTargets(Ceo, [Member, -1]));
        Assert.Throws<InvalidDataException>(() =>
            CorporationMembershipRules.CheckRemovalTargets(
                Ceo,
                Enumerable.Repeat(Member, CorporationMembershipRules.MaximumMemberRemoval + 1).ToArray()
            )
        );
        CorporationMembershipRules.CheckRemovalTargets(
            Ceo,
            Enumerable.Repeat(Member, CorporationMembershipRules.MaximumMemberRemoval).ToArray()
        );
    }

    private static void PrepareAdditionalPilots(TestWorld world) =>
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);

    private static long CreatePilot(TestWorld world, string name) =>
        world.Services.GetRequiredService<ICharacterCreation>().Create(1000001, new(name, 1, 1, 1, 64, [1], [2], 18));

    private static void Join(TestWorld world, long pilotId)
    {
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var invitation = membership.Invite(Ceo, pilotId);
        membership.Update(
            pilotId,
            new(invitation.Id, pilotId, invitation.CorporationId, "", CorporationApplicationStatus.Joined, "", invitation.AppliedAt)
        );
    }
}
