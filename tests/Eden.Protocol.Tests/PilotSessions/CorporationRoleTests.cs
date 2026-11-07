using Eden.Data.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationRoleTests
{
    internal static object?[] MemberArguments(long pilot, CorporationRoleSet roles, long? title = 0, bool? block = null) =>
        [
            pilot,
            null,
            null,
            null,
            roles.Roles,
            roles.GrantableRoles,
            roles.RolesAtHQ,
            roles.GrantableRolesAtHQ,
            roles.RolesAtBase,
            roles.GrantableRolesAtBase,
            roles.RolesAtOther,
            roles.GrantableRolesAtOther,
            null,
            title,
            block,
        ];

    private static CorporationMemberUpdate Update(long pilot, CorporationRoleSet roles, long? title = 0, bool? block = null) =>
        new(
            pilot,
            null,
            null,
            null,
            new(
                roles.Roles,
                roles.GrantableRoles,
                roles.RolesAtHQ,
                roles.GrantableRolesAtHQ,
                roles.RolesAtBase,
                roles.GrantableRolesAtBase,
                roles.RolesAtOther,
                roles.GrantableRolesAtOther
            ),
            null,
            title,
            block
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeRoleTitleRowsRefreshSessionAndPersistWithoutChangingWalletOrMembership(bool liveWorld)
    {
        using TestWorld world = new(liveWorld: liveWorld);
        int id = CorporationAdministrationTests.CreateAndJoin(world);
        var roles = world.Services.GetRequiredService<ICorporationRoles>();
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            var header = new List<object?>
            {
                "titleID",
                "titleName",
                "roles",
                "grantableRoles",
                "rolesAtHQ",
                "grantableRolesAtHQ",
                "rolesAtBase",
                "grantableRolesAtBase",
                "rolesAtOther",
                "grantableRolesAtOther",
            };
            BlueInstance rows = new(
                "eve.common.script.sys.rowset.Rowset",
                new Dictionary<string, object?>
                {
                    ["header"] = header,
                    ["lines"] = new List<object?>
                    {
                        new List<object?> { 1L, "Explorers 星", (long)CorporationRole.Accountant, 0L, 0L, 0L, 0L, 0L, 0L, 0L },
                    },
                }
            );
            Assert.Null(ceo.Reply("UpdateTitles", [rows]));
            var direct = new CorporationRoleSet(CorporationRules.PersonnelManagerRole, RolesAtHQ: (long)CorporationRole.HangarCanQuery1);
            Assert.Null(ceo.Reply("UpdateMember", MemberArguments(90000002, direct, 1)));
            Assert.Null(ceo.Reply("UpdateMembers", [new List<object?> { MemberArguments(90000002, direct, 1).Take(14).ToList() }]));
            member.Advance();
            var identity = member.Context.Pilot.Identity;
            Assert.Equal(
                CorporationRules.PersonnelManagerRole | (long)CorporationRole.Accountant | (long)CorporationRole.HangarCanQuery1,
                identity.CorporationRoles
            );
            Assert.Equal((long)CorporationRole.HangarCanQuery1, identity.CorporationAccess!.Roles.RolesAtHQ);
            var titles = Assert.IsType<Dictionary<long, object?>>(member.Reply("GetTitles", []));
            Assert.Equal(CorporationRoleRules.TitleCount, titles.Count);
            var title = Assert.IsType<BlueInstance>(titles[1]);
            Assert.Equal("util.Row", title.Name);
            var state = BlueRead.Tuple(title.State, 2);
            var titleFields = Assert
                .IsType<List<object?>>(state[0])
                .Select(BlueMarshal.Text)
                .Zip(Assert.IsType<List<object?>>(state[1]))
                .ToDictionary(p => p.First, p => p.Second);
            Assert.Equal("Explorers 星", BlueMarshal.Text(titleFields["titleName"]));
            Assert.Equal((long)CorporationRole.Accountant, titleFields["roles"]);
            var memberFields = BlueRead.Fields(Assert.IsType<BlueInstance>(member.Reply("GetMember", [90000002L])).State);
            Assert.Equal(1L, memberFields["titleMask"]);
            Assert.Equal((long)CorporationRole.HangarCanQuery1, memberFields["rolesAtHQ"]);
            var publicResult = member.Call("GetInfoWindowDataForChar", [90000002L]);
            var publicFields = BlueRead.Fields(
                Assert
                    .IsType<BlueInstance>(
                        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(publicResult).Reply))
                    )
                    .State
            );
            Assert.Equal("Explorers 星", BlueMarshal.Text(publicFields["title1"]));
            Assert.Null(publicFields["title2"]);
            Assert.DoesNotContain("grantableRoles", publicFields.Keys);
            Assert.Single(member.Context.Notifications.OfType<CorporationTitleChanged>());
            Assert.Single(member.Context.Notifications.OfType<CorporationRolesChanged>());
            member.Advance();
            Assert.Single(member.Context.Notifications.OfType<CorporationRolesChanged>());
            var corporation = world.Services.GetRequiredService<IPlayerCorporations>();
            Assert.Equal(id, corporation.Identity(90000002).Id);
            Assert.Equal(2, corporation.Read(id)!.MemberCount);
            Assert.Single(
                world.Services.GetRequiredService<Eden.Gameplay.Market.IPilotWalletReader>().Journal(90000001).CorporationPayments!
            );
        }
        var expected = world.Services.GetRequiredService<IPlayerCorporations>().Identity(90000002);
        world.Services.Dispose();
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        Assert.Equal(expected, reopened.GetRequiredService<IPlayerCorporations>().Identity(90000002));
        Assert.Equal("Explorers 星", reopened.GetRequiredService<ICorporationRoles>().Titles(90000002)[0].Name);
    }

    [Fact]
    public void DelegationCannotEscalateGrantablesDirectorOrCeoAndBulkFailureRollsBackEveryRow()
    {
        using TestWorld world = new();
        int id = CorporationAdministrationTests.CreateAndJoin(world);
        var roles = world.Services.GetRequiredService<ICorporationRoles>();
        roles.UpdateMembers(90000001, [Update(90000002, new(GrantableRoles: (long)CorporationRole.Accountant))]);
        roles.UpdateMembers(90000002, [Update(90000002, new((long)CorporationRole.Accountant, (long)CorporationRole.Accountant))]);
        var corporations = world.Services.GetRequiredService<IPlayerCorporations>();
        var before = corporations.Identity(90000002);
        foreach (
            var invalid in new[]
            {
                Update(90000002, new(CorporationRules.DirectorRole)),
                Update(90000002, new((long)CorporationRole.Accountant, (long)CorporationRole.PersonnelManager)),
                Update(90000002, new((long)CorporationRole.PersonnelManager)),
                Update(90000001, new()),
                Update(99999999, new()),
                Update(90000002, new(-1)),
                Update(90000002, new(RolesAtHQ: CorporationRules.DirectorRole)),
                Update(90000002, new(64)),
                Update(90000002, new(), 1L << CorporationRoleRules.TitleCount),
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => roles.UpdateMembers(90000002, [invalid]));
            Assert.Equal(before, corporations.Identity(90000002));
        }
        Assert.Throws<InvalidDataException>(() =>
            roles.UpdateMembers(90000001, [Update(90000002, new((long)CorporationRole.PersonnelManager)), Update(99999999, new())])
        );
        Assert.Equal(before, corporations.Identity(90000002));
        Assert.Throws<InvalidDataException>(() => roles.UpdateTitles(90000002, [new(1, "Forged", new())]));
        Assert.Throws<InvalidDataException>(() => roles.UpdateTitles(90000001, [new(1, "Director", new(CorporationRules.DirectorRole))]));
        roles.UpdateMembers(90000001, [Update(90000002, new(CorporationRules.DirectorRole))]);
        Assert.Equal(CorporationRules.CeoRoles, corporations.Identity(90000002).Roles);
        world.Services.GetRequiredService<ICorporationAdministration>().Update(90000002, new("Director edit", "", 0.2, true, 0));
        Assert.Equal("Director edit", corporations.Read(id)!.Details.Description);
        Assert.Throws<InvalidDataException>(() => roles.UpdateMembers(90000002, [Update(90000001, new())]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingRolesBlocksReassignmentAndLeavingUntilThePersisted24HourBoundary(bool liveWorld)
    {
        TestClock clock = new();
        using TestWorld world = new(liveWorld: liveWorld, clock: clock);
        int id = CorporationAdministrationTests.CreateAndJoin(world);
        var roles = world.Services.GetRequiredService<ICorporationRoles>();
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        roles.UpdateMembers(90000001, [Update(90000002, new((long)CorporationRole.Accountant))]);
        Assert.False(membership.CanRemove(90000002, 90000002).Allowed);
        using (CorporationTestClient client = new(world, 1000002))
        {
            var leave = BlueRead.Tuple(client.Reply("CanLeaveCurrentCorporation", []), 3);
            Assert.False(Assert.IsType<bool>(leave[0]));
            Assert.Equal("CrpCantQuitNotInStasis", BlueMarshal.Text(leave[1]));
            // Exact base_corporation.RemoveAllRoles request, including blockRoles=1.
            Assert.Null(client.Reply("UpdateMember", MemberArguments(90000002, new(), 0, true)));
        }
        Assert.False(membership.CanRemove(90000001, 90000002).Allowed);
        Assert.Throws<InvalidDataException>(() => membership.Remove(90000002, 90000002));
        Assert.Throws<InvalidDataException>(() => roles.UpdateMembers(90000001, [Update(90000002, new((long)CorporationRole.Accountant))]));
        world.Services.Dispose();
        WorldInitialization.Initialize(new(world.Path), clock);
        using var reopened = ServerServices.Build(new(world.Path), clock);
        var savedMembership = reopened.GetRequiredService<ICorporationMembership>();
        clock.AdvanceWallTime(CorporationRoleRules.RemovalDelay - TimeSpan.FromTicks(1));
        Assert.False(savedMembership.CanRemove(90000002, 90000002).Allowed);
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.True(savedMembership.CanRemove(90000002, 90000002).Allowed);
        savedMembership.Remove(90000002, 90000002);
        Assert.Equal(1000044, reopened.GetRequiredService<IPlayerCorporations>().Identity(90000002).Id);
        Assert.Equal(1, reopened.GetRequiredService<IPlayerCorporations>().Read(id)!.MemberCount);
        using WorldDbContext check = new(world.Path);
        Assert.Empty(check.Set<SavedCorporationMemberAccess>().Where(m => m.PilotId == 90000002));
    }

    [Fact]
    public void TitleEditingAndDeletionRecomputeRightsAndEmitChangesOnce()
    {
        using TestWorld world = new();
        CorporationAdministrationTests.CreateAndJoin(world);
        var roles = world.Services.GetRequiredService<ICorporationRoles>();
        using CorporationTestClient member = new(world, 1000002);
        roles.UpdateTitles(90000001, [new(1, "Recruiter", new(CorporationRules.PersonnelManagerRole))]);
        roles.UpdateMembers(90000001, [Update(90000002, new(), 1)]);
        member.Advance();
        Assert.Equal(CorporationRules.PersonnelManagerRole, member.Context.Pilot.Identity.CorporationRoles);
        roles.UpdateTitles(90000001, [new(1, "Accountant", new((long)CorporationRole.Accountant))]);
        member.Advance();
        Assert.Equal((long)CorporationRole.Accountant, member.Context.Pilot.Identity.CorporationRoles);
        roles.DeleteTitle(90000001, 1);
        member.Advance();
        Assert.Equal(0, member.Context.Pilot.Identity.CorporationRoles);
        Assert.Equal(0, member.Context.Pilot.Identity.CorporationAccess!.TitleMask);
        Assert.NotNull(member.Context.Pilot.Identity.CorporationAccess.RolesRemovedAt);
        Assert.Equal(3, member.Context.Notifications.OfType<CorporationRolesChanged>().Count());
        Assert.Equal(3, member.Context.Notifications.OfType<CorporationTitleChanged>().Count());
        member.Advance();
        Assert.Equal(3, member.Context.Notifications.OfType<CorporationTitleChanged>().Count());
        Assert.All(roles.Titles(90000002), title => Assert.False(title.Roles.HasAuthority));
    }

    [Fact]
    public void Version74UpgradePreservesExistingCorporationsAndStartsOrdinaryMembersWithoutPrivileges()
    {
        using TestWorld world = new();
        int id = CorporationAdministrationTests.CreateAndJoin(world);
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldCorporationMemberAccess;
                DROP TABLE WorldCorporationTitle;
                ALTER TABLE WorldCorporationChange DROP COLUMN RoleJson;
                ALTER TABLE WorldCorporationChange DROP COLUMN TitleJson;
                PRAGMA user_version=74;
                """
            );
        }
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        Assert.Equal(0, reopened.GetRequiredService<IPlayerCorporations>().Identity(90000002).Roles);
        Assert.Equal(id, reopened.GetRequiredService<IPlayerCorporations>().Identity(90000002).Id);
        reopened.GetRequiredService<ICorporationRoles>().UpdateMembers(90000001, [Update(90000002, new((long)CorporationRole.Accountant))]);
        Assert.Equal((long)CorporationRole.Accountant, reopened.GetRequiredService<IPlayerCorporations>().Identity(90000002).Roles);
        using WorldDbContext check = new(world.Path);
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Single(check.Set<SavedCorporationPayment>());
    }

    [Fact]
    public async Task RoleRefreshPreservesTheCurrentCorporationBindingAndAnnouncesScopedSessionFields()
    {
        using TestWorld world = new();
        int id = CorporationAdministrationTests.CreateAndJoin(world);
        var roles = world.Services.GetRequiredService<ICorporationRoles>();
        using var scope = world.Services.CreateScope();
        Queue<object?> input = new([
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000002L]),
            PilotServicesTests.Request("corpRegistry", "MachoBindObject", [(long)id, null]),
            PilotServicesTests.Request(null, "GetMember", [90000002L], "N=10001:42-2"),
            PilotServicesTests.Request(null, "GetMember", [90000002L], "N=10001:42-2"),
        ]);
        int read = 0;
        List<BlueInstance> packets = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            scope
                .ServiceProvider.GetRequiredService<IPilotSession>()
                .RunAsync(
                    () =>
                    {
                        if (++read == 3)
                        {
                            roles.UpdateMembers(90000001, [Update(90000002, new(RolesAtHQ: (long)CorporationRole.HangarCanQuery1))]);
                        }
                        return input.TryDequeue(out var packet) ? Task.FromResult(packet) : throw new EndOfStreamException();
                    },
                    packet =>
                    {
                        packets.Add(Assert.IsType<BlueInstance>(packet));
                        return Task.CompletedTask;
                    },
                    _ => { },
                    1000002,
                    42,
                    SignedSettings.Empty,
                    CancellationToken.None
                )
        );
        Assert.DoesNotContain(packets, p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        Assert.Equal(4, packets.Count(p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal)));
        var changes = packets.Where(p => p.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, changes.Length);
        var fields = BlueRead.Fields(BlueRead.Tuple(BlueRead.Tuple(BlueRead.Tuple(changes[1].State, 14)[4], 3)[1], 2)[1]);
        Assert.Equal(new object?[] { 0L, (long)CorporationRole.HangarCanQuery1 }, fields["corprole"]);
        Assert.Equal(new object?[] { 0L, (long)CorporationRole.HangarCanQuery1 }, fields["rolesAtHQ"]);
        Assert.DoesNotContain("rolesAtAll", fields.Keys);
    }
}
