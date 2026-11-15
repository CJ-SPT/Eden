using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Corporations;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationMemberQueryTests
{
    [Fact]
    public void NativeQueriesSeparateDirectImpliedAndTitleNameSearches()
    {
        using TestWorld world = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        var roles = world.Services.GetRequiredService<ICorporationRoles>();
        roles.UpdateTitles(90000001, [new(1, "Surveyors", new((long)CorporationRole.Accountant))]);
        Assert.Null(ceo.Reply("UpdateMember", CorporationRoleTests.MemberArguments(90000002, new(), 1)));
        List<object?> role = [new List<object?> { "roles", 7L, (long)CorporationRole.Accountant }];
        Assert.Empty(Assert.IsType<List<object?>>(member.Reply("GetMemberIDsByQuery", [role, false, false])));
        Assert.Equal(
            new object?[] { 90000001L, 90000002L },
            Assert.IsType<List<object?>>(member.Reply("GetMemberIDsByQuery", [role, true, false]))
        );
        List<object?> title = [new List<object?> { "characterID", 9L, "survey" }];
        Assert.Empty(Assert.IsType<List<object?>>(member.Reply("GetMemberIDsByQuery", [title, true, false])));
        Assert.Equal(new object?[] { 90000002L }, Assert.IsType<List<object?>>(member.Reply("GetMemberIDsByQuery", [title, false, true])));
        Assert.Empty(Assert.IsType<List<object?>>(member.Reply("GetMemberIDsByQuery", [new List<object?>(), false, false])));
        Assert.Equal(
            new object?[] { 90000002L },
            Assert.IsType<List<object?>>(
                member.Reply(
                    "GetMemberIDsByQuery",
                    [
                        new List<object?>
                        {
                            new List<object?> { "titleMask", 7L, 1L },
                            new List<object?> { 2L, "baseID", 1L, null },
                        },
                        false,
                        false,
                    ]
                )
            )
        );
        Assert.IsType<PilotCallResult.Failure>(
            member.Call(
                "GetMemberIDsByQuery",
                [
                    new List<object?>
                    {
                        new List<object?> { "roles; DELETE", 7L, 1L },
                    },
                    false,
                    false,
                ]
            )
        );
        Assert.IsType<PilotCallResult.Failure>(
            member.Call(
                "GetMemberIDsByQuery",
                [
                    new List<object?>
                    {
                        new List<object?> { 2L, "roles", 7L, 1L },
                    },
                    false,
                    false,
                ]
            )
        );
        Assert.IsType<PilotCallResult.Failure>(member.Call("GetMemberIDsByQuery", [role, false, false], new() { ["unknown"] = true }));
    }

    [Fact]
    public void ShareholderQueryUsesIssuedSupplyAndCurrentMembership()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var shares = world.Services.GetRequiredService<ICorporationShares>();
        var queries = world.Services.GetRequiredService<ICorporationMemberQueries>();
        shares.Transfer(90000001, new(corporation, true, 90000002, 500));
        Assert.Empty(queries.AboveAverageShareholders(90000001));
        shares.Transfer(90000001, new(corporation, true, 90000002, 1));
        Assert.Equal(new long[] { 90000002 }, queries.AboveAverageShareholders(90000001));
        shares.Transfer(90000002, new(corporation, false, 90000001, 2));
        Assert.Empty(queries.AboveAverageShareholders(90000001));
    }

    [Fact]
    public void TrackingShowsRealPresenceAndProtectsLocationsAndExactTimes()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            var guests = member.Services.GetRequiredService<StationGuestHandler>();
            guests.Advance(member.Context);
            var online = Tracking(ceo, "GetMemberTrackingInfo", 90000002);
            Assert.Equal(-1.0, online["lastOnline"]);
            Assert.Equal(clock.Now, online["logonDateTime"]);
            Assert.Equal((long)member.Context.Pilot.ShipTypeId, online["shipTypeID"]);
            Assert.Equal(member.Context.Pilot.LocationId, online["locationID"]);
            Assert.IsType<PilotCallResult.Failure>(member.Call("GetMemberTrackingInfo", []));
            var simple = Tracking(member, "GetMemberTrackingInfoSimple", 90000001);
            Assert.Null(simple["locationID"]);
            Assert.Null(simple["shipTypeID"]);
            Assert.Null(simple["logonDateTime"]);
            Assert.Null(simple["logoffDateTime"]);
            clock.AdvanceWallTime(TimeSpan.FromHours(1));
            guests.Advance(member.Context);
            Assert.Equal(clock.Now - TimeSpan.TicksPerHour, Tracking(ceo, "GetMemberTrackingInfo", 90000002)["logonDateTime"]);
            guests.Dispose();
            long logout = clock.Now;
            clock.AdvanceWallTime(TimeSpan.FromMinutes(90));
            var offline = Tracking(ceo, "GetMemberTrackingInfo", 90000002);
            Assert.Equal(1.5, offline["lastOnline"]);
            Assert.Equal(logout, offline["logoffDateTime"]);
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock: clock, liveWorld: true);
        var row = reopened
            .GetRequiredService<ICorporationMemberQueries>()
            .Tracking(90000001, true)
            .Single(row => row.Member.PilotId == 90000002);
        Assert.Equal(clock.Now - (long)(1.5 * TimeSpan.TicksPerHour), row.Activity.LoggedOffAt);
        Assert.Equal(clock.Now - (long)(2.5 * TimeSpan.TicksPerHour), row.Activity.LoggedOnAt);
    }

    [Fact]
    public async Task SessionDisconnectSavesLogoffAndClearsOnlinePresence()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        using var scope = world.Services.CreateScope();
        int reads = 0;
        long loggedOn = clock.Now;
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            scope
                .ServiceProvider.GetRequiredService<IPilotSession>()
                .RunAsync(
                    () =>
                    {
                        if (++reads == 1)
                        {
                            return Task.FromResult<object?>(PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000002L]));
                        }
                        clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
                        throw new EndOfStreamException();
                    },
                    _ => Task.CompletedTask,
                    _ => { },
                    1000002,
                    42,
                    SignedSettings.Empty,
                    CancellationToken.None
                )
        );
        var member = world
            .Services.GetRequiredService<ICorporationMemberQueries>()
            .Tracking(90000001, true)
            .Single(row => row.Member.PilotId == 90000002);
        Assert.Equal(loggedOn, member.Activity.LoggedOnAt);
        Assert.Equal(clock.Now, member.Activity.LoggedOffAt);
        Assert.False(world.Services.GetRequiredService<SharedStationGuests>().IsOnline(90000002));
    }

    [Fact]
    public void Version160MigrationPreservesPilotsWithUnknownHistoricalActivity()
    {
        using TestWorld world = new(liveWorld: true);
        CorporationAdministrationTests.CreateAndJoin(world);
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw("DROP TABLE WorldPilotActivity; PRAGMA user_version=160;");
        }
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        Assert.All(
            reopened.GetRequiredService<ICorporationMemberQueries>().Tracking(90000001, true),
            row =>
            {
                Assert.Null(row.Activity.LoggedOnAt);
                Assert.Null(row.Activity.LoggedOffAt);
            }
        );
        reopened.GetRequiredService<IPilotActivity>().Record(90000002, true);
        using WorldDbContext check = new(world.Path);
        Assert.Single(check.Set<SavedPilotActivity>());
    }

    [Theory]
    [InlineData(CorporationQueryOperator.Contains, "den", true)]
    [InlineData(CorporationQueryOperator.Is, "EDEN PILOT", true)]
    [InlineData(CorporationQueryOperator.StartsWith, "Eden", true)]
    [InlineData(CorporationQueryOperator.EndsWith, "pilot", true)]
    [InlineData(CorporationQueryOperator.Like, "E_en%", true)]
    [InlineData(CorporationQueryOperator.Like, "%x", false)]
    public void NameOperatorsHaveBoundedCaseInsensitiveBehavior(CorporationQueryOperator operation, string text, bool expected)
    {
        CorporationMember member = new(1, "Eden Pilot", 1, 100, 0);
        CorporationMemberCriterion term = new(null, CorporationQueryField.Name, operation, Text: text);
        CorporationMemberQueryRules.Validate(new([term], false, false));
        Assert.Equal(expected, CorporationMemberQueryRules.Matches(member, [term]));
    }

    private static Dictionary<string, object?> Tracking(CorporationTestClient client, string method, long pilotId)
    {
        var reply = Assert.IsType<PilotCallResult.Success>(client.Call(method, []));
        var list = Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)));
        return list.Select(row => BlueRead.Fields(Assert.IsType<BlueInstance>(row).State))
            .Single(fields => Assert.IsType<long>(fields["characterID"]) == pilotId);
    }
}
