using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class SkillInjectorServiceTests
{
    [Fact]
    public void FirstInjectorCallInitializesThePointNotificationBaselineBeforeConsumption()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Context.Select(db.Store.FindPilot(1000001)!);
        Assert.Null(Reply(client.Call("InjectSkillpoints", [SkillInjectorTestData.Item(db), 1L], "@skills")));
        Assert.Equal(500_000, Assert.Single(client.Context.Notifications.OfType<FreeSkillPointsChanged>()).Balance);
    }

    [Fact]
    public void NativeAllowanceReadIsNumericAndInjectorPreviewCannotSpoofItsCachedCount()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 80_000_000);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        Assert.Same(PilotCallResult.Unsupported, client.Call("GetAvailableNonDiminishingInjections", [], "nonDiminishingInjectionMgr"));
        client.Refresh();
        Assert.Equal(0L, Reply(client.Call("GetAvailableNonDiminishingInjections", [], "nonDiminishingInjectionMgr")));
        Assert.Equal(80_000_000L, Reply(client.Call("GetSkillPoints", [], "@skills")));
        Assert.Equal(150_000L, Reply(client.Call("GetDiminishedSpFromInjectors", [40520L, 1L, 999L], "@skills")));
        Assert.Equal(0L, Reply(client.Call("GetFreeSkillPoints", [], "@skills")));
        long item = SkillInjectorTestData.Item(db);
        Assert.Null(Reply(client.Call("CheckInjectionConstraints", [item, 1L], "@skills")));
        client.Context.Notifications.Clear();
        Assert.Null(Reply(client.Call("InjectSkillpoints", [item, 1L], "@skills")));
        Assert.Equal(150_000, Assert.Single(client.Context.Notifications.OfType<FreeSkillPointsChanged>()).Balance);
        Assert.Single(client.Context.Notifications, n => n.Name == "OnItemChange");
        Assert.Single(client.Context.Notifications.OfType<SkillQueueSaved>());
        Assert.Equal(150_000L, Reply(client.Call("GetFreeSkillPoints", [], "@skills")));
        client.Context.Notifications.Clear();
        Assert.Equal(
            "YouDoNotOwnCharacterServiceItem",
            Assert.IsType<PilotCallResult.NamedFailure>(client.Call("InjectSkillpoints", [item, 1L], "@skills")).Name
        );
        Assert.Empty(client.Context.Notifications);
    }

    [Fact]
    public void NativeAugmentorConsumptionAndInjectorSpendingKeepBothSessionsCountersAndPoolsCurrent()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 80_000_000);
        using CourierServiceTests.Client writer = new(db, clock, 1000001);
        using CourierServiceTests.Client observer = new(db, clock, 1000001);
        writer.Refresh();
        observer.Refresh();
        writer.Context.Notifications.Clear();
        observer.Context.Notifications.Clear();
        long booster = SkillInjectorTestData.Item(db, SkillInjectorTestData.Augmentor);
        Assert.Null(Reply(writer.Call("UseBooster", [booster, HomeStationTestData.Origin], "dogmaIM")));
        Assert.Equal(3, Assert.Single(writer.Context.Notifications.OfType<NonDiminishingInjectionsAdded>()).Amount);
        Assert.Equal(3L, Reply(observer.Call("GetAvailableNonDiminishingInjections", [], "nonDiminishingInjectionMgr")));
        Assert.Equal(3, Assert.Single(observer.Context.Notifications.OfType<NonDiminishingInjectionsAdded>()).Amount);
        Assert.Equal(500_000L, Reply(observer.Call("GetDiminishedSpFromInjectors", [40520L, 1L, 0L], "@skills")));
        long injectors = SkillInjectorTestData.Item(db, quantity: 4);
        writer.Context.Notifications.Clear();
        observer.Context.Notifications.Clear();
        Assert.Null(Reply(writer.Call("InjectSkillpoints", [injectors, 4L], "@skills")));
        Assert.Equal(3, Assert.Single(writer.Context.Notifications.OfType<NonDiminishingInjectionsUsed>()).Amount);
        Assert.Equal(1_650_000, Assert.Single(writer.Context.Notifications.OfType<FreeSkillPointsChanged>()).Balance);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Equal(3, Assert.Single(observer.Context.Notifications.OfType<NonDiminishingInjectionsUsed>()).Amount);
        Assert.Equal(1_650_000, Assert.Single(observer.Context.Notifications.OfType<FreeSkillPointsChanged>()).Balance);
        observer.Context.Notifications.Clear();
        observer.Advance();
        Assert.Empty(observer.Context.Notifications);
    }

    [Fact]
    public void ConsumingAndSpendingAllAllowancesBetweenPollsResetsTheNativeCounter()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 80_000_000);
        using CourierServiceTests.Client writer = new(db, clock, 1000001);
        using CourierServiceTests.Client observer = new(db, clock, 1000001);
        writer.Refresh();
        observer.Refresh();
        observer.Context.Notifications.Clear();
        long booster = SkillInjectorTestData.Item(db, SkillInjectorTestData.Augmentor);
        Assert.Null(Reply(writer.Call("UseBooster", [booster, HomeStationTestData.Origin], "dogmaIM")));
        Assert.Null(Reply(writer.Call("InjectSkillpoints", [SkillInjectorTestData.Item(db, quantity: 3), 3L], "@skills")));
        observer.Advance();
        Assert.Single(observer.Context.Notifications.OfType<NonDiminishingInjectionsRemoved>());
        Assert.Equal(0L, Reply(observer.Call("GetAvailableNonDiminishingInjections", [], "nonDiminishingInjectionMgr")));
        Assert.DoesNotContain(observer.Context.Notifications.OfType<NonDiminishingInjectionsAdded>(), n => n.Amount != 0);
    }

    [Fact]
    public void DailyAvailabilityNotificationFiresOnceAtTheSavedDeadlineAndUsesNativeErrorNames()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 0, grade: AccountCloneGrade.Alpha);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        long item = SkillInjectorTestData.Item(db, (int)SkillInjectorType.DailyAlpha, 2);
        Assert.Equal(0L, Reply(client.Call("GetNextAvailableInjection", [], "alphaInjectorMgr")));
        Assert.Null(Reply(client.Call("InjectSkillpoints", [item, 1L], "@skills")));
        long next = clock.Now + TimeSpan.TicksPerDay;
        Assert.Equal(next, Reply(client.Call("GetNextAvailableInjection", [], "alphaInjectorMgr")));
        Assert.Equal(
            "AlreadyInjectedToday",
            Assert.IsType<PilotCallResult.NamedFailure>(client.Call("CheckInjectionConstraints", [item, 1L], "@skills")).Name
        );
        client.Context.Notifications.Clear();
        clock.AdvanceWallTime(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        client.Advance();
        Assert.Empty(client.Context.Notifications.OfType<AlphaInjectionAvailable>());
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        // The regular poll may be one second away; a native read refreshes immediately.
        client.Call("GetNextAvailableInjection", [], "alphaInjectorMgr");
        Assert.Single(client.Context.Notifications.OfType<AlphaInjectionAvailable>());
        client.Context.Notifications.Clear();
        client.Call("GetNextAvailableInjection", [], "alphaInjectorMgr");
        Assert.Empty(client.Context.Notifications);
        Assert.Null(Reply(client.Call("InjectSkillpoints", [item, 1L], "@skills")));
    }

    [Fact]
    public void NativeSplitCombineAndCapFailurePreserveExpectedWireValues()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 1_500_000);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        long mini = SkillInjectorTestData.Item(db, 43630);
        var error = Assert.IsType<PilotCallResult.NamedFailure>(client.Call("CheckInjectionConstraints", [mini, 1L], "@skills"));
        Assert.Equal("InjectorSkillPointLimitReached", error.Name);
        var values = Assert.IsType<Dictionary<string, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(error.Values)));
        Assert.Equal(43630L, values["typeID"]);
        Assert.Equal(1_500_000L, values["limit"]);
        client.Context.Notifications.Clear();
        long large = SkillInjectorTestData.Item(db, quantity: 2);
        Assert.Null(Reply(client.Call("SplitSkillInjector", [large, 2L], "@skills")));
        Assert.Equal(2, client.Context.Notifications.Count(n => n.Name == "OnItemChange"));
        var small = db
            .Services.GetRequiredService<Eden.Gameplay.Inventory.IPilotInventory>()
            .List(SkillInjectorTestData.Pilot, HomeStationTestData.Origin)
            .Single(i => i.TypeId == 45635);
        Assert.Equal(10, small.Quantity);
        client.Context.Notifications.Clear();
        Assert.Null(Reply(client.Call("CombineSkillInjector", [small.Id, 10L], "@skills")));
        Assert.Equal(2, client.Context.Notifications.Count(n => n.Name == "OnItemChange"));
        Assert.Equal(0L, Reply(client.Call("GetFreeSkillPoints", [], "@skills")));
    }

    [Fact]
    public void OfflineCompletionsAreIncludedInBothTheTierAndNativeSkillNotifications()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 4_999_750);
        ImplantTestData.Training(db).Save(SkillInjectorTestData.Pilot, [new(3327, 1)], true);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        client.Context.Notifications.Clear();
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.Null(Reply(client.Call("InjectSkillpoints", [SkillInjectorTestData.Item(db), 1L], "@skills")));
        Assert.Single(client.Context.Notifications.OfType<ServerSkillsChanged>());
        Assert.Equal(400_000, Assert.Single(client.Context.Notifications.OfType<FreeSkillPointsChanged>()).Balance);
        Assert.Equal(5_000_000L, Reply(client.Call("GetSkillPoints", [], "@skills")));
    }

    [Theory]
    [InlineData("@skills", "InjectSkillpoints")]
    [InlineData("@skills", "GetDiminishedSpFromInjectors")]
    [InlineData("@skills", "CheckInjectionConstraints")]
    [InlineData("nonDiminishingInjectionMgr", "GetAvailableNonDiminishingInjections")]
    [InlineData("alphaInjectorMgr", "GetNextAvailableInjection")]
    public void InvalidNativeArgumentsDoNotCreditAnything(string service, string method)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        client.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(client.Call(method, ["bad"], service));
        Assert.IsType<PilotCallResult.Failure>(client.Call(method, [], service, new() { ["unexpected"] = true }));
        Assert.Empty(client.Context.Notifications);
        Assert.Equal(0L, Reply(client.Call("GetFreeSkillPoints", [], "@skills")));
    }

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return BlueMarshal.Decode(BlueMarshal.Encode(((PilotCallResult.Success)result).Reply));
    }
}
