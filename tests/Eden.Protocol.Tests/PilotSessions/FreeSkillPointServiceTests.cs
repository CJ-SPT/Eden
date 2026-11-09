using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Redemption;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class FreeSkillPointServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SilentBountyCreditsPreserveLaterRewardAndSpendNotifications(bool resident)
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        using LiveWorld? live = resident ? new(new(db.Path), clock, _ => { }) : null;
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        client.Refresh();
        client.Context.Notifications.Clear();
        using (WorldDbContext world = new(db.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            PilotTrainingStore.CreditSilentFreePoints(world, FreePointTestData.Pilot, 250);
            world.SaveChanges();
            transaction.Commit();
        }
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Advance();
        Assert.Empty(client.Context.Notifications.OfType<FreeSkillPointsChanged>());
        Assert.Empty(client.Context.Notifications.OfType<ServerSkillsChanged>());
        Assert.Equal(250, FreePointTestData.Points(db).Balance(FreePointTestData.Pilot));

        FreePointTestData.Credit(db);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Advance();
        Assert.Equal(1250, Assert.Single(client.Context.Notifications.OfType<FreeSkillPointsChanged>()).Balance);
        client.Context.Notifications.Clear();
        Assert.Equal(1150L, Reply(client.Call("ApplyFreeSkillPoints", [3327L, 100L], "@skills")));
        Assert.Equal(1150, Assert.Single(client.Context.Notifications.OfType<FreeSkillPointsChanged>()).Balance);
    }

    [Fact]
    public void NativeRedeemedSkillPointsAreVisibleAfterSelectionWithoutAnInventoryDelivery()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        var store = RedemptionTests.Store(db);
        var token = store.Grant(RedemptionTests.Grant("sp", 54652, 2));
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        Assert.Same(PilotCallResult.Unsupported, client.Call("GetFreeSkillPoints", [], "@skills"));
        var rows = Assert.IsType<List<object?>>(Reply(client.Call("GetRedeemTokens", [], "userSvc")));
        var row = Assert.IsType<BlueInstance>(Assert.Single(rows));
        Assert.Equal(true, Assert.IsType<Dictionary<string, object?>>(row.State)["isAutoInject"]);
        var report = Assert.IsType<Dictionary<string, object?>>(
            Reply(client.Call("ClaimRedeemTokens", [new List<object?> { Selection(token) }, FreePointTestData.Pilot], "userSvc"))
        );
        Assert.Equal(2000L, report["skillpoints"]);
        Assert.Null(report["stationID"]);
        Assert.Empty(Assert.IsType<List<object?>>(report["itemsCreated"]));
        Assert.Empty(Assert.IsType<List<object?>>(report["errors"]));
        Assert.Single(client.Context.Notifications.OfType<RedeemingQueueUpdated>());
        Assert.DoesNotContain(client.Context.Notifications, n => n.Name == "OnItemChange");
        client.Refresh();
        Assert.Equal(2000L, Reply(client.Call("GetFreeSkillPoints", [], "@skills")));
        var queue = Assert.IsType<object?[]>(Reply(client.Call("GetSkillQueueAndFreePoints", [], "@skills")));
        Assert.Empty(Assert.IsType<List<object?>>(queue[0]));
        Assert.Equal(2000L, queue[1]);
    }

    [Fact]
    public void NativeTargetPreviewAndApplicationSendOneBalanceSkillBrainAndQueueUpdate()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        FreePointTestData.Credit(db);
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        client.Refresh();
        client.Context.Notifications.Clear();
        object?[] args = [new List<object?> { new object?[] { 3327L, 1L } }];
        var preview = Assert.IsType<Dictionary<long, object?>>(Reply(client.Call("GetFreeSkillPointsAppliedToSkills", args, "@skills")));
        Assert.Equal(250L, preview[3327]);
        Assert.Empty(client.Context.Notifications);
        Assert.Equal(1000, FreePointTestData.Points(db).Balance(FreePointTestData.Pilot));
        Assert.Null(Reply(client.Call("ApplyFreeSkillPointsToSkills", args, "@skills")));
        Assert.Equal(750, Assert.Single(client.Context.Notifications.OfType<FreeSkillPointsChanged>()).Balance);
        Assert.Single(client.Context.Notifications.OfType<ServerSkillsChanged>());
        Assert.Single(client.Context.Notifications, n => n.Name == "OnServerBrainUpdated");
        Assert.Single(client.Context.Notifications.OfType<SkillQueueSaved>());
        client.Context.Notifications.Clear();
        Assert.Null(Reply(client.Call("ApplyFreeSkillPointsToSkills", args, "@skills")));
        Assert.Empty(client.Context.Notifications);
        Assert.Equal(750L, Reply(client.Call("GetFreeSkillPoints", [], "@skills")));
        var history = Assert.IsType<List<object?>>(Reply(client.Call("GetSkillHistory", [], "@skills")));
        Assert.Single(history);
    }

    [Fact]
    public void NativeQueueApplicationUsesElapsedPointsAndReplacesTheActiveDeadline()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        FreePointTestData.Credit(db);
        FreePointTestData.Training(db).Save(FreePointTestData.Pilot, [new(3327, 1), new(3327, 2)], true);
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        client.Refresh();
        client.Call("GetSkillQueueAndFreePoints", [], "@skills");
        clock.AdvanceWallTime(TimeSpan.FromSeconds(120));
        var preview = Assert.IsType<Dictionary<long, object?>>(Reply(client.Call("GetFreeSkillPointsAppliedToQueue", [], "@skills")));
        Assert.Equal(1000L, preview[3327]);
        Assert.Null(Reply(client.Call("ApplyFreeSkillPointsToQueue", [], "@skills")));
        Assert.Equal(0, Assert.Single(client.Context.Notifications.OfType<FreeSkillPointsChanged>()).Balance);
        Assert.Equal(clock.UtcNow.AddSeconds(1540).UtcDateTime.ToFileTimeUtc(), client.Context.NextSkillCompletion);
        Assert.Equal(1030, Assert.Single(db.Services.GetRequiredService<IPilotSkillReader>().ReadSkills(FreePointTestData.Pilot)).Points);
    }

    [Fact]
    public void NativeSingleApplicationReturnsTheRemainingBalanceAndRejectsForgedShapesWithoutChanges()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        FreePointTestData.Credit(db);
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        client.Refresh();
        foreach (
            var args in new object?[][] { [3327L, 0L], [3327L, -1L], [3327L, 1001L], [999999L, 1L], [3327L, 1.0], [3327L, 1L, 90000002L] }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(client.Call("ApplyFreeSkillPoints", args, "@skills"));
        }
        Assert.IsType<PilotCallResult.Failure>(client.Call("GetFreeSkillPoints", [90000002L], "@skills"));
        Assert.IsType<PilotCallResult.Failure>(
            client.Call("ApplyFreeSkillPoints", [3327L, 1L], "@skills", new() { ["pilotID"] = 90000002L })
        );
        Assert.Empty(client.Context.Notifications);
        Assert.Equal(1000, FreePointTestData.Points(db).Balance(FreePointTestData.Pilot));
        Assert.Equal(500L, Reply(client.Call("ApplyFreeSkillPoints", [3327L, 500L], "@skills")));
        Assert.Equal(500, Assert.Single(client.Context.Notifications.OfType<FreeSkillPointsChanged>()).Balance);
        Assert.Equal(0, FreePointTestData.Points(db).Balance(90000002));
    }

    [Theory]
    [InlineData("poll")]
    [InlineData("balance")]
    [InlineData("snapshot")]
    public void ExternalCreditThenSpendWithNoNetPoolChangeStillRefreshesSkillsAndBrainExactlyOnce(string refresh)
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        using CourierServiceTests.Client other = new(db, clock, 1000002);
        client.Refresh();
        other.Refresh();
        client.Context.Notifications.Clear();
        other.Context.Notifications.Clear();
        FreePointTestData.Credit(db);
        FreePointTestData.Points(db).Apply(FreePointTestData.Pilot, new FreePointRequest.Single(3327, 1000));
        if (refresh == "poll")
        {
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            client.Advance();
        }
        else
        {
            client.Call(refresh == "balance" ? "GetFreeSkillPoints" : "GetSkillQueueAndFreePoints", [], "@skills");
        }
        other.Advance();
        var update = Assert.Single(client.Context.Notifications.OfType<ServerSkillsChanged>());
        Assert.Equal(1000, update.Skills.Entries[3327].Skill.Points);
        Assert.Single(client.Context.Notifications, n => n.Name == "OnServerBrainUpdated");
        Assert.Empty(client.Context.Notifications.OfType<FreeSkillPointsChanged>());
        Assert.Empty(other.Context.Notifications.OfType<ServerSkillsChanged>());
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Advance();
        Assert.Single(client.Context.Notifications.OfType<ServerSkillsChanged>());
    }

    [Fact]
    public void MixedClaimsInSpaceCreditSkillPointsAndKeepInventoryTokensPending()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        var pilot = db.Store.FindPilot(RedemptionTests.Account)!;
        db.Store.SetPosition(pilot.ShipId, new(1, 2, 3), pilot.SystemId);
        var store = RedemptionTests.Store(db);
        var points = store.Grant(RedemptionTests.Grant("sp", 54652, 1));
        var item = store.Grant(RedemptionTests.Grant("item", 34));
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        client.Refresh();
        var result = Assert.IsType<PilotCallResult.Success>(
            client.Call("ClaimRedeemTokens", [new List<object?> { Selection(points), Selection(item) }, FreePointTestData.Pilot], "userSvc")
        );
        Assert.NotEmpty(BlueMarshal.Encode(result.Reply));
        var report = Assert.IsType<Dictionary<string, object?>>(WireAssertions.Materialize(result));
        Assert.Equal(1000L, report["skillpoints"]);
        Assert.Empty(Assert.IsType<List<object?>>(report["itemsCreated"]));
        Assert.Single(Assert.IsType<List<object?>>(report["errors"]));
        Assert.Equal(item, Assert.Single(store.Read(RedemptionTests.Account)));
        Assert.Equal(1000, Assert.Single(client.Context.Notifications.OfType<FreeSkillPointsChanged>()).Balance);
        Assert.DoesNotContain(client.Context.Notifications, n => n.Name == "OnItemChange");
    }

    private static Dictionary<string, object?> Selection(RedeemToken token) =>
        new()
        {
            ["tokenID"] = token.Id,
            ["massTokenID"] = null,
            ["typeID"] = token.TypeId,
        };

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return BlueMarshal.Decode(BlueMarshal.Encode(((PilotCallResult.Success)result).Reply));
    }
}
