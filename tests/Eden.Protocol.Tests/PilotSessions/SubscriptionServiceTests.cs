using Eden.Data.World.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class SubscriptionServiceTests
{
    private static readonly string[] StoreMethods = ["get_offers", "get_products", "get_categories"];

    [Fact]
    public void StoreCatalogsAreUnavailableWithoutChangingSavedStateOrAcceptingPurchases()
    {
        using TestWorld world = new();
        var pilot = world.Store.FindPilot(1000001)!;
        using var scope = world.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        for (int phase = 0; phase < 2; phase++)
        {
            foreach (string method in StoreMethods)
            {
                foreach (long storeId in new long[] { 4, 5, 9 })
                {
                    Assert.Empty(Assert.IsType<List<object?>>(Decode(Call(method, new object?[] { storeId }))));
                }
                foreach (
                    object? args in new object?[]
                    {
                        null,
                        Array.Empty<object?>(),
                        new List<object?> { 4L },
                        new object?[] { null },
                        new object?[] { "4" },
                        new object?[] { true },
                        new object?[] { 0L },
                        new object?[] { 1L },
                        new object?[] { long.MaxValue },
                        new object?[] { 4L, 5L },
                    }
                )
                {
                    Assert.IsType<PilotCallResult.Failure>(Call(method, args));
                }
                Assert.IsType<PilotCallResult.Failure>(Call(method, new object?[] { 4L }, new() { ["accountID"] = 1000002L }));
            }
            Assert.Same(PilotCallResult.Unsupported, Call("buy_offer", new object?[] { 4L }));
            Assert.Same(PilotCallResult.Unsupported, Call("unknown_store_method", new object?[] { 4L }));
            if (!context.Selected)
            {
                context.Select(pilot);
            }
        }
        Assert.Equal(pilot, context.Pilot);
        Assert.Equal(pilot, world.Store.FindPilot(1000001));
        Assert.Empty(context.Notifications);
        Assert.Empty(context.NotificationsBeforeFlight);

        PilotCallResult Call(string method, object? args, Dictionary<string, object?>? keywords = null) =>
            router.Call(
                context,
                "storeManager",
                method,
                BlueMarshal.Decode(BlueMarshal.Encode(args)),
                _ => { },
                keywords ?? new() { ["machoVersion"] = 1L }
            );
    }

    private static readonly string[] CheckoutMethods =
    [
        "GetTestingConfiguration",
        "IsFastCheckoutEnabledForUser",
        "ClearFastCheckoutCacheForUser",
    ];

    [Fact]
    public void FastCheckoutReadsStayDisabledAcrossSelectionAndCacheResets()
    {
        using TestWorld world = new();
        var pilot = world.Store.FindPilot(1000001)!;
        using var scope = world.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        for (int phase = 0; phase < 2; phase++)
        {
            for (int cycle = 0; cycle < 2; cycle++)
            {
                foreach (string method in CheckoutMethods)
                {
                    AssertCheckoutReply(method, Decode(Call(method, Array.Empty<object?>(), new() { ["machoVersion"] = 1L })));
                }
            }
            foreach (string method in CheckoutMethods)
            {
                foreach (object? args in new object?[] { null, new List<object?>(), new object?[] { null }, new object?[] { 1000002L } })
                {
                    Assert.IsType<PilotCallResult.Failure>(Call(method, args));
                }
                Assert.IsType<PilotCallResult.Failure>(Call(method, Array.Empty<object?>(), new() { ["accountID"] = 1000002L }));
            }
            Assert.Same(PilotCallResult.Unsupported, Call("BuyOffer", Array.Empty<object?>()));
            Assert.Same(PilotCallResult.Unsupported, Call("UnknownCheckoutMethod", Array.Empty<object?>()));
            if (!context.Selected)
            {
                context.Select(pilot);
            }
        }
        Assert.Equal(pilot, context.Pilot);
        Assert.Equal(pilot, world.Store.FindPilot(1000001));
        Assert.Empty(context.Notifications);
        Assert.Empty(context.NotificationsBeforeFlight);

        PilotCallResult Call(string method, object? args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, "FastCheckoutService", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
    }

    [Theory]
    [InlineData(AccountCloneGrade.Alpha, false)]
    [InlineData(AccountCloneGrade.Omega, false)]
    [InlineData(AccountCloneGrade.Alpha, true)]
    [InlineData(AccountCloneGrade.Omega, true)]
    public void SubscriptionTimeMatchesSavedGradeBeforeAndAfterSelectionWithoutExpiring(AccountCloneGrade grade, bool inSpace)
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero));
        using TestWorld world = new(clock: clock);
        SaveGrade(world, grade);
        var savedPilot = world.Store.FindPilot(1000001)!;
        var otherAccount = world.Store.ReadAccount(1000002);
        var pilot = inSpace ? savedPilot with { Position = new SpacePosition(1_000_000, 0, 0) } : savedPilot;
        using var scope = world.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            world.Store.ReadAccount(1000001),
            [pilot],
            p => new(world.Store, p, clock, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        long before = ReadTime();
        context.Select(pilot);
        Assert.Equal(before, ReadTime());
        clock.AdvanceWallTime(TimeSpan.FromDays(3660));
        Assert.Equal(before, ReadTime());
        Assert.Equal((long)grade, Decode(Call("GetCloneGrade", Array.Empty<object?>())));

        foreach (object? args in new object?[] { null, new List<object?>(), new object?[] { null }, new object?[] { pilot.PilotId } })
        {
            Assert.IsType<PilotCallResult.Failure>(Call("GetSubscriptionTime", args));
        }
        Assert.IsType<PilotCallResult.Failure>(Call("GetSubscriptionTime", Array.Empty<object?>(), new() { ["accountID"] = 1000002L }));
        Assert.Same(PilotCallResult.Unsupported, Call("UnknownSubscriptionMethod", Array.Empty<object?>()));
        Assert.Same(
            PilotCallResult.Unsupported,
            router.Call(context, "unrelatedService", "GetSubscriptionTime", Array.Empty<object?>(), _ => { })
        );
        Assert.Equal(pilot, context.Pilot);
        Assert.Equal(savedPilot, world.Store.FindPilot(1000001));
        Assert.Empty(context.Notifications);
        Assert.Empty(context.NotificationsBeforeFlight);

        // Open a separate store to verify reads did not create an expiry or change either account.
        var reopened = new Eden.World.Docking.SqliteWorld(new WorldDatabase(world.Path), clock);
        Assert.Equal(grade, reopened.ReadAccount(1000001).CloneGrade);
        Assert.Equal(otherAccount, reopened.ReadAccount(1000002));

        long ReadTime()
        {
            long expiry = Assert.IsType<long>(Decode(Call("GetSubscriptionTime", Array.Empty<object?>(), new() { ["machoVersion"] = 1L })));
            AssertExpiry(grade, expiry);
            return expiry;
        }

        PilotCallResult Call(string method, object? args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, "subscriptionMgr", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
    }

    [Theory]
    [InlineData(AccountCloneGrade.Alpha)]
    [InlineData(AccountCloneGrade.Omega)]
    public async Task NativePilotServicesPacketsReturnSubscriptionTimeWithoutDestinationErrors(AccountCloneGrade grade)
    {
        using TestWorld world = new();
        SaveGrade(world, grade);
        Queue<object?> input = new(
            new[]
            {
                PilotServicesTests.Request("subscriptionMgr", "GetCloneGrade", []),
                PilotServicesTests.Request("subscriptionMgr", "GetSubscriptionTime", []),
                PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
                PilotServicesTests.Request("userSvc", "GetMultiCharactersTrainingSlots", []),
                PilotServicesTests.Request("subscriptionMgr", "GetSubscriptionTime", []),
                PilotServicesTests.Request("FastCheckoutService", "GetTestingConfiguration", []),
                PilotServicesTests.Request("FastCheckoutService", "IsFastCheckoutEnabledForUser", []),
                PilotServicesTests.Request("FastCheckoutService", "ClearFastCheckoutCacheForUser", []),
                PilotServicesTests.Request("storeManager", "get_offers", [4L]),
                PilotServicesTests.Request("storeManager", "get_products", [4L]),
                PilotServicesTests.Request("storeManager", "get_categories", [4L]),
            }.Select(packet => BlueMarshal.Decode(BlueMarshal.Encode(packet)))
        );
        List<BlueInstance> output = [];
        using var scope = world.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                () => input.TryDequeue(out var packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
                packet =>
                {
                    output.Add(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(packet))));
                    return Task.CompletedTask;
                },
                _ => { },
                1000001,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        Assert.DoesNotContain(output, packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var replies = output
            .Where(packet => packet.Name.EndsWith(".CallRsp", StringComparison.Ordinal))
            .Select(packet => BlueMarshal.Decode(BlueRead.Bytes(BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1)[0])))
            .ToArray();
        Assert.Equal(11, replies.Length);
        Assert.Equal((long)grade, replies[0]);
        AssertExpiry(grade, Assert.IsType<long>(replies[1]));
        Assert.Null(replies[2]);
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(replies[3]));
        Assert.Equal(replies[1], replies[4]);
        for (int index = 0; index < CheckoutMethods.Length; index++)
        {
            AssertCheckoutReply(CheckoutMethods[index], replies[index + 5]);
        }
        Assert.All(replies.Skip(8), reply => Assert.Empty(Assert.IsType<List<object?>>(reply)));
    }

    private static void AssertCheckoutReply(string method, object? reply)
    {
        switch (method)
        {
            case "GetTestingConfiguration":
                var configuration = BlueRead.Tuple(reply, 3);
                Assert.False(Assert.IsType<bool>(configuration[0]));
                Assert.Equal(string.Empty, BlueMarshal.Text(configuration[1]));
                Assert.False(Assert.IsType<bool>(configuration[2]));
                break;
            case "IsFastCheckoutEnabledForUser":
                Assert.False(Assert.IsType<bool>(reply));
                break;
            default:
                Assert.Null(reply);
                break;
        }
    }

    private static object? Decode(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private static void AssertExpiry(AccountCloneGrade grade, long expiry)
    {
        if (grade == AccountCloneGrade.Alpha)
        {
            Assert.Equal(0L, expiry);
            return;
        }
        var date = DateTime.FromFileTimeUtc(expiry);
        Assert.Equal(9999, date.Year);
        Assert.Equal(12, date.Month);
        Assert.Equal(31, date.Day);
        Assert.Equal(TimeSpan.Zero, date.TimeOfDay);
    }

    private static void SaveGrade(TestWorld world, AccountCloneGrade grade)
    {
        using WorldDbContext saved = new(world.Path);
        saved.Set<Account>().Find(1000001L)!.CloneGrade = grade;
        saved.SaveChanges();
    }
}
