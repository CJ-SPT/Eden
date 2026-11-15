using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationMemberAuditTests
{
    private const long Ceo = 90000001;
    private const long Member = 90000002;

    [Fact]
    public void NativeAuditReturnsTwoRowsetsAndSubsequentCorporationReadsSucceed()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        var result = ceo.CallService(
            "corpmgr",
            "AuditMember",
            [Ceo, clock.Now - TimeSpan.TicksPerDay, clock.Now + TimeSpan.TicksPerDay, 10L]
        );
        var success = Assert.IsType<PilotCallResult.Success>(result);
        var audit = Assert.IsType<CorporationMemberAuditReply>(success.Reply);
        Assert.Equal(CorporationAuditEventType.Created, Assert.Single(audit.Result.Events).Type);
        var wire = BlueRead.Tuple(audit.ToBlue(), 2);
        var events = Assert.IsType<BlueConstructor>(wire[0]);
        var roles = Assert.IsType<BlueConstructor>(wire[1]);
        Assert.Equal("carbon.common.script.sys.crowset.CRowset", events.Name);
        Assert.Equal(events.Name, roles.Name);
        Assert.Single(Assert.IsType<List<object?>>(events.Arguments[1]));
        Assert.Empty(Assert.IsType<List<object?>>(roles.Arguments[1]));
        Assert.NotEmpty(BlueMarshal.Encode(audit));
        var eventValues = Assert.IsType<BlueConstructor>(Assert.Single(Assert.IsType<List<object?>>(events.Arguments[1])));
        Assert.Equal(
            new object?[] { audit.Result.Events[0].Id, clock.Now, (int)CorporationAuditEventType.Created, (int)Ceo, corporation },
            Assert.IsType<object?[]>(eventValues.Arguments[1])
        );
        Assert.NotNull(ceo.Reply("GetMembersPaged", [1L]));
        Assert.Equal(corporation, ceo.Context.Pilot.CorpId);
        // An empty date interval result must still contain both iterable native rowsets.
        var empty = Assert.IsType<CorporationMemberAuditReply>(
            Assert
                .IsType<PilotCallResult.Success>(
                    ceo.CallService("corpmgr", "AuditMember", [Member, clock.Now + 1, clock.Now + TimeSpan.TicksPerDay, 10L])
                )
                .Reply
        );
        Assert.Empty(empty.Result.Events);
        Assert.Empty(empty.Result.Roles);
        Assert.NotEmpty(BlueMarshal.Encode(empty));
    }

    [Fact]
    public void RoleAndTitleChangesAreDatedOnceSearchableBySubjectOrIssuerAndSurviveCheckpoint()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var roles = world.Services.GetRequiredService<ICorporationRoles>();
        using (CorporationTestClient ceo = new(world, 1000001))
        {
            clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
            Assert.Null(
                ceo.Reply(
                    "UpdateMember",
                    CorporationRoleTests.MemberArguments(
                        Member,
                        new((long)CorporationRole.Accountant, (long)CorporationRole.HangarCanQuery1)
                    )
                )
            );
            long changed = clock.Now;
            var reader = world.Services.GetRequiredService<ICorporationMemberAudit>();
            var entries = reader.Read(Ceo, new(Member, changed, changed + 1, 10)).Roles;
            Assert.Equal(2, entries.Count);
            Assert.All(
                entries,
                row => Assert.Equal((Member, corporation, changed, Ceo), (row.PilotId, row.CorporationId, row.Time, row.IssuerId))
            );
            Assert.Equal((0L, (long)CorporationRole.Accountant), (entries[0].Before, entries[0].After));
            Assert.True(entries[1].Grantable);
            Assert.Equal(entries, reader.Read(Ceo, new(Ceo, changed, changed + 1, 10)).Roles);
            Assert.Single(reader.Read(Ceo, new(Member, changed, changed + 1, 1)).Roles);
            Assert.Empty(reader.Read(Ceo, new(Member, 0, changed, 10)).Roles);
            Assert.Null(
                ceo.Reply(
                    "UpdateMember",
                    CorporationRoleTests.MemberArguments(
                        Member,
                        new((long)CorporationRole.Accountant, (long)CorporationRole.HangarCanQuery1)
                    )
                )
            );
            Assert.Equal(2, reader.Read(Ceo, new(Member, changed, changed + 1, 10)).Roles.Count);
            clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
            roles.UpdateTitles(Ceo, [new(1, "Survey", new((long)CorporationRole.HangarCanQuery2))]);
            Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new(), 1)));
            clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
            roles.DeleteTitle(Ceo, 1);
            Assert.Equal(5, reader.Read(Ceo, new(Member, changed, clock.Now + 1, 10)).Roles.Count);
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        var persisted = reopened.GetRequiredService<ICorporationMemberAudit>().Read(Ceo, new(Member, 0, clock.Now + 1, 10));
        Assert.Equal(5, persisted.Roles.Count);
        Assert.Single(persisted.Events);
    }

    [Fact]
    public void AuditAuthorizationUsesCurrentResidentRolesAndReadsDoNotWriteOrOpenSql()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        object?[] args = [Member, 0L, clock.Now + 1, 10L];
        Assert.IsType<PilotCallResult.Failure>(member.CallService("corpmgr", "AuditMember", args));
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new((long)CorporationRole.Auditor))));
        Assert.IsType<PilotCallResult.Success>(member.CallService("corpmgr", "AuditMember", args));
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(Member, new())));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("corpmgr", "AuditMember", args));
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        long head = membership.Head(Ceo);
        var reader = world.Services.GetRequiredService<ICorporationMemberAudit>();
        _ = reader.Read(Ceo, new(Member, 0, clock.Now + 1, 10));
        using (var measured = SessionPerformance.Begin("member audit", _ => { }, force: true)!)
        {
            _ = reader.Read(Ceo, new(Member, 0, clock.Now + 1, 10));
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
        }
        Assert.Equal(head, membership.Head(Ceo));
        foreach (
            object?[] invalid in new object?[][]
            {
                [],
                [Member],
                [Member, 0L, 0L, 10L],
                [Member, -1L, clock.Now, 10L],
                [Member, 0L, clock.Now, 0L],
                [Member, 0L, clock.Now, 1001L],
                [Member, null, clock.Now, 10L],
                ["member", 0L, clock.Now, 10L],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(ceo.CallService("corpmgr", "AuditMember", invalid));
        }
        Assert.IsType<PilotCallResult.Failure>(ceo.CallService("corpmgr", "AuditMember", args, new() { ["force"] = true }));
        ceo.Context.LogOff();
        Assert.Same(PilotCallResult.Unsupported, ceo.CallService("corpmgr", "AuditMember", args));
    }

    [Fact]
    public void CharacterInfoMembershipDateMatchesEmploymentAfterJoinAndRejoin()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var sheet = world.Services.GetRequiredService<IPilotCharacterSheet>();
        clock.AdvanceWallTime(TimeSpan.FromDays(2));
        membership.Remove(Ceo, Member);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        var invitation = membership.Invite(Ceo, Member);
        membership.Update(
            Member,
            new(invitation.Id, Member, corporation, "", CorporationApplicationStatus.Joined, "", invitation.AppliedAt)
        );
        using (CorporationTestClient ceo = new(world, 1000001))
        {
            var reply = Assert.IsType<PilotCallResult.Success>(ceo.CallService("charMgr", "GetPublicInfo3", [Member]));
            var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply))).State);
            var header = Assert.IsType<List<object?>>(fields["header"]);
            var row = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(fields["lines"])));
            Assert.Equal(clock.Now, row[header.Select(BlueMarshal.Text).ToList().IndexOf("startDateTime")]);
            var publicInfo = Assert.IsType<PilotCallResult.Success>(ceo.CallService("charMgr", "GetPublicInfo", [Member]));
            var born = BlueRead.Fields(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(publicInfo.Reply))).State)[
                "createDateTime"
            ];
            Assert.Equal(sheet.ReadPublicProfile(Member)!.CreatedAt, born);
            Assert.NotEqual(clock.Now, born);
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        Assert.Equal(clock.Now, reopened.GetRequiredService<IPilotCharacterSheet>().ReadPublicProfile(Member)!.CorporationJoinedAt);
    }
}
