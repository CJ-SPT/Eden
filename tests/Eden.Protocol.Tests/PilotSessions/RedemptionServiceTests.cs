using Eden.Gameplay.Redemption;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Redemption;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class RedemptionServiceTests
{
    [Fact]
    public void AccountQueueAndNativeClaimReportWorkBeforeSelectionWithPrivatePerTokenErrors()
    {
        var clock = CalendarTestData.Clock();
        using var db = RedemptionTests.Create(clock);
        var store = RedemptionTests.Store(db);
        var one = store.Grant(RedemptionTests.Grant());
        var bad = store.Grant(RedemptionTests.Grant("mismatch"));
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        using CourierServiceTests.Client other = new(db, clock, 1000002);
        var tokens = Assert.IsType<List<object?>>(Reply(client.Call("GetRedeemTokens", [], "userSvc")));
        Assert.Equal(2, tokens.Count);
        var token = Fields(tokens[0]);
        Assert.Equal(one.Id, token["tokenID"]);
        Assert.Null(token["massTokenID"]);
        Assert.Equal(34L, token["typeID"]);
        Assert.Equal(10L, token["quantity"]);
        Assert.Equal("Test reward", Text(token["label"]));
        Assert.Equal("Reward description", Text(token["description"]));
        Assert.Equal(clock.Now, token["dateTime"]);
        Assert.Null(token["expireDateTime"]);
        Assert.Null(token["stationID"]);
        Assert.Null(token["blueprintInfo"]);
        Assert.Equal(false, token["soulbound"]);
        Assert.Equal(false, token["isAutoInject"]);
        Assert.Empty(Assert.IsType<List<object?>>(Reply(other.Call("GetRedeemTokens", [], "userSvc"))));
        var claim = Assert.IsType<PilotCallResult.Success>(
            client.Call(
                "ClaimRedeemTokens",
                [new List<object?> { Selection(one), Selection(bad, 35) }, RedemptionTests.Pilot],
                "userSvc",
                new() { ["useHomeStation"] = false }
            )
        );
        Assert.NotEmpty(BlueMarshal.Encode(claim.Reply));
        // The inbound decoder intentionally never instantiates exception reductions.
        // Inspect the outbound exception constructor just as transport errors do.
        var result = Assert.IsType<Dictionary<string, object?>>(WireAssertions.Materialize(claim));
        Assert.Equal(RedemptionTests.Pilot, result["charID"]);
        Assert.Equal((long)db.Store.FindPilot(RedemptionTests.Account)!.StationId, result["stationID"]);
        Assert.Equal(0, result["skillpoints"]);
        var created = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(result["itemsCreated"])));
        Assert.Equal(34, created["typeID"]);
        Assert.Equal(10, created["quantity"]);
        Assert.Equal(result["stationID"], created["stationID"]);
        long item = Assert.IsType<long>(created["itemID"]);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Equal(RedemptionTests.Pilot, world.Set<OwnedItem>().Find(item)!.OwnerId);
        }
        var error = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(result["errors"])));
        Assert.Equal(bad.Id, error[0]);
        var exception = Assert.IsType<BlueConstructor>(error[1]);
        Assert.Equal("exceptions.UserError", exception.Name);
        Assert.Equal("CustomInfo", Text(exception.Arguments[0]));
        Assert.False(client.Context.Selected);
        Assert.Single(client.Context.Notifications.OfType<RedeemingQueueUpdated>());
        Assert.DoesNotContain(client.Context.Notifications, n => n.Name == "OnItemChange");
        Assert.Equal(bad, Assert.Single(store.Read(RedemptionTests.Account)));
        Assert.Empty(other.Context.Notifications);
    }

    [Fact]
    public void SelectedClaimsReversalAndTrashSendNativeQueueAndInventoryChangesOnlyAfterCommit()
    {
        var clock = CalendarTestData.Clock();
        using var db = RedemptionTests.Create(clock);
        var store = RedemptionTests.Store(db);
        var token = store.Grant(RedemptionTests.Grant());
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        client.Refresh();
        var result = Assert.IsType<Dictionary<string, object?>>(
            Reply(client.Call("ClaimRedeemTokens", [new List<object?> { Selection(token) }, RedemptionTests.Pilot], "userSvc"))
        );
        var item = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(result["itemsCreated"])));
        Assert.Single(client.Context.Notifications, n => n.Name == "OnItemChange");
        Assert.Single(client.Context.Notifications.OfType<RedeemingQueueUpdated>());
        client.Context.Notifications.Clear();
        Assert.Null(Reply(client.Call("ReverseRedeem", [item["itemID"]], "userSvc")));
        Assert.Single(client.Context.Notifications, n => n.Name == "OnItemChange");
        Assert.Single(client.Context.Notifications.OfType<RedeemingQueueUpdated>());
        var returned = Assert.Single(store.Read(RedemptionTests.Account));
        client.Context.Notifications.Clear();
        Assert.Null(Reply(client.Call("TrashRedeemTokens", [new List<object?> { new object?[] { returned.Id, null } }], "userSvc")));
        Assert.Single(client.Context.Notifications.OfType<RedeemingQueueUpdated>());
        Assert.DoesNotContain(client.Context.Notifications, n => n.Name == "OnItemChange");
        Assert.Empty(store.Read(RedemptionTests.Account));
    }

    [Fact]
    public void PollingDetectsExternalGrantsConsumptionAndExpiryOnceWithoutNotifyingOtherAccounts()
    {
        var clock = CalendarTestData.Clock();
        using var db = RedemptionTests.Create(clock);
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        using CourierServiceTests.Client other = new(db, clock, 1000002);
        client.Call("GetRedeemTokens", [], "userSvc");
        other.Call("GetRedeemTokens", [], "userSvc");
        using var independent = ServerServices.Build(new(db.Path), clock);
        var writer = independent.GetRequiredService<IAccountRedemption>();
        var token = writer.Grant(RedemptionTests.Grant(expires: clock.Now + 2 * TimeSpan.TicksPerSecond));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Advance();
        other.Advance();
        client.Advance();
        Assert.Single(client.Context.Notifications.OfType<RedeemingQueueUpdated>());
        Assert.Empty(other.Context.Notifications);
        client.Context.Notifications.Clear();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        client.Advance();
        client.Advance();
        Assert.Single(client.Context.Notifications.OfType<RedeemingQueueUpdated>());
        Assert.Empty(RedemptionTests.Store(db).Read(RedemptionTests.Account));
        Assert.True(token.Id > 0);
    }

    [Fact]
    public async Task TheRealSessionPollsAndDeliversQueueUpdatesBeforeAnyPilotIsSelected()
    {
        var clock = CalendarTestData.Clock();
        using var db = RedemptionTests.Create(clock);
        using var scope = db.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<object?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<BlueInstance> notification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool first = true;
        var run = session.RunAsync(
            () =>
            {
                if (first)
                {
                    first = false;
                    return Task.FromResult<object?>(PilotServicesTests.Request("userSvc", "GetRedeemTokens", []));
                }
                waiting.TrySetResult();
                return pending.Task;
            },
            packet =>
            {
                var value = Assert.IsType<BlueInstance>(packet);
                if (value.Name.EndsWith(".Notification", StringComparison.Ordinal))
                {
                    notification.TrySetResult(value);
                }
                return Task.CompletedTask;
            },
            _ => { },
            RedemptionTests.Account,
            42,
            SignedSettings.Empty,
            CancellationToken.None
        );
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            RedemptionTests.Store(db).Grant(RedemptionTests.Grant());
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            var packet = await notification.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var state = BlueRead.Tuple(packet.State, 14);
            var destination = Assert.IsType<BlueInstance>(state[2]);
            Assert.Equal("OnRedeemingQueueUpdated", BlueRead.Tuple(destination.State, 4)[1]);
            Assert.Equal(RedemptionTests.Account, state[3]);
        }
        finally
        {
            pending.TrySetException(new EndOfStreamException());
            await Assert.ThrowsAsync<EndOfStreamException>(() => run);
        }
    }

    [Fact]
    public void MalformedRequestsAndForgedFieldsFailWithoutConsumingTokensOrEmittingEvents()
    {
        var clock = CalendarTestData.Clock();
        using var db = RedemptionTests.Create(clock);
        var token = RedemptionTests.Store(db).Grant(RedemptionTests.Grant());
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        foreach (
            var (method, args) in new (string, object?[])[]
            {
                ("GetRedeemTokens", [RedemptionTests.Account]),
                ("ClaimRedeemTokens", [new List<object?> { Selection(token) }, 90000002L]),
                ("ClaimRedeemTokens", [new List<object?> { Selection(token), Selection(token) }, RedemptionTests.Pilot]),
                ("ClaimRedeemTokens", [new object?[] { Selection(token) }, RedemptionTests.Pilot]),
                (
                    "ClaimRedeemTokens",
                    [new List<object?> { new Dictionary<string, object?> { ["tokenID"] = token.Id } }, RedemptionTests.Pilot]
                ),
                (
                    "TrashRedeemTokens",
                    [
                        new List<object?>
                        {
                            new List<object?> { token.Id, null },
                        },
                    ]
                ),
                ("ReverseRedeem", [1L]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(client.Call(method, args, "userSvc"));
        }
        Assert.IsType<PilotCallResult.Failure>(client.Call("GetRedeemTokens", [], "userSvc", new() { ["accountID"] = 1000002L }));
        Assert.IsType<PilotCallResult.Failure>(
            client.Call(
                "ClaimRedeemTokens",
                [new List<object?> { Selection(token) }, RedemptionTests.Pilot],
                "userSvc",
                new() { ["useHomeStation"] = 1L }
            )
        );
        Assert.Equal(token, Assert.Single(RedemptionTests.Store(db).Read(RedemptionTests.Account)));
        Assert.Empty(client.Context.Notifications);
    }

    private static Dictionary<string, object?> Selection(RedeemToken token, int? type = null) =>
        new()
        {
            ["tokenID"] = token.Id,
            ["massTokenID"] = null,
            ["typeID"] = type ?? token.TypeId,
        };

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return BlueMarshal.Decode(BlueMarshal.Encode(((PilotCallResult.Success)result).Reply));
    }

    private static Dictionary<string, object?> Fields(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State);

    private static string Text(object? value) => BlueMarshal.Text(value);
}
