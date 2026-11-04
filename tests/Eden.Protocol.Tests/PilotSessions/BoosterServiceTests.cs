using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class BoosterServiceTests
{
    [Fact]
    public void NativeConsumptionAndReadsRefreshAttributesBoosterCacheAndThePredictedQueue()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        BoosterTestData.ShortLifetime(db);
        long item = BoosterTestData.Item(db);
        ImplantTestData.Training(db).Save(BoosterTestData.Pilot, [new(3327, 1)], true);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        Assert.Same(PilotCallResult.Unsupported, client.Call("GetBoosters", [], "@skills"));
        client.Refresh();
        Assert.Empty(Map(Reply(client.Call("GetBoosters", [], "@skills"))));
        client.Context.Notifications.Clear();
        Assert.Null(Reply(client.Call("UseBooster", [item, HomeStationTestData.Origin], "dogmaIM")));
        Assert.Single(client.Context.Notifications.OfType<ItemChanged>());
        Assert.Empty(Assert.Single(client.Context.Notifications.OfType<ServerBoostersChanged>()).Arguments);
        Assert.Single(client.Context.Notifications.OfType<SkillQueueSaved>());
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(440).Ticks, client.Context.NextSkillCompletion);
        Assert.Equal(
            30,
            client
                .Context.Notifications.OfType<ModuleAttributesChanged>()
                .SelectMany(n => n.Changes)
                .Single(c => c.Attribute == DogmaAttributeId.Perception)
                .Value
        );
        var booster = Fields(Assert.Single(Map(Reply(client.Call("GetBoosters", [], "@skills")))).Value);
        Assert.Equal(item, booster["boosterID"]);
        Assert.Equal((long)BoosterTestData.Master, booster["boosterTypeID"]);
        Assert.Equal(10L, booster["boosterSlot"]);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(120).Ticks, booster["expiryTime"]);
        Assert.Equal(30L, Map(Reply(client.Call("GetAttributes", [], "@skills")))[(long)DogmaAttributeId.Perception]);
        using var scope = db.Services.CreateScope();
        Assert.Equal(
            30,
            scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimePilot(client.Context.Pilot).Attributes[
                DogmaAttributeId.Perception
            ]
        );
        client.Context.Notifications.Clear();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(120));
        client.Advance();
        Assert.Single(client.Context.Notifications.OfType<ServerBoostersChanged>());
        Assert.Empty(Map(Reply(client.Call("GetBoosters", [], "@skills"))));
        Assert.Equal(20L, Map(Reply(client.Call("GetAttributes", [], "@skills")))[(long)DogmaAttributeId.Perception]);
        client.Context.Notifications.Clear();
        client.Advance();
        Assert.Empty(client.Context.Notifications);
    }

    [Fact]
    public void ForcedReadsAndAnotherSessionObserveConsumptionAndExpiryWithoutDuplicateEvents()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        BoosterTestData.ShortLifetime(db, 1);
        using CourierServiceTests.Client writer = new(db, clock, 1000001);
        using CourierServiceTests.Client observer = new(db, clock, 1000001);
        writer.Refresh();
        observer.Refresh();
        observer.Context.Notifications.Clear();
        long item = BoosterTestData.Item(db);
        Assert.Null(Reply(writer.Call("UseBooster", [item, HomeStationTestData.Origin], "dogmaIM")));
        Assert.Single(Map(Reply(observer.Call("GetBoosters", [], "@skills"))));
        Assert.Single(observer.Context.Notifications.OfType<ServerBoostersChanged>());
        observer.Context.Notifications.Clear();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Single(observer.Context.Notifications.OfType<ServerBoostersChanged>());
        observer.Context.Notifications.Clear();
        // The next booster is consumed and expires between observer polls.
        item = BoosterTestData.Item(db);
        Assert.Null(Reply(writer.Call("UseBooster", [item, HomeStationTestData.Origin], "dogmaIM")));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Single(observer.Context.Notifications.OfType<ServerBoostersChanged>());
        Assert.Empty(Map(Reply(observer.Call("GetBoosters", [], "@skills"))));
        observer.Context.Notifications.Clear();
        observer.Advance();
        Assert.Empty(observer.Context.Notifications);
    }

    [Fact]
    public void InstallingAnImplantWhileBoostedPublishesTheCombinedAttributes()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        BoosterTestData.Consume(db, BoosterTestData.Item(db));
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        client.Context.Notifications.Clear();
        long implant = ImplantTestData.Item(db);
        Assert.Null(Reply(client.Call("InjectImplant", [implant], "dogmaIM")));
        Assert.Equal(
            33,
            client
                .Context.Notifications.OfType<ModuleAttributesChanged>()
                .SelectMany(n => n.Changes)
                .Single(c => c.Attribute == DogmaAttributeId.Perception)
                .Value
        );
        Assert.Equal(33L, Map(Reply(client.Call("GetAttributes", [], "@skills")))[(long)DogmaAttributeId.Perception]);
    }

    [Theory]
    [InlineData("@skills", "GetBoosters")]
    [InlineData("dogmaIM", "UseBooster")]
    public void InvalidNativeShapesAndKeywordsLeaveBoosterStateEmpty(string service, string method)
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        client.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(client.Call(method, ["bad"], service));
        Assert.IsType<PilotCallResult.Failure>(client.Call(method, [], service, new() { ["unexpected"] = true }));
        Assert.Empty(client.Context.Notifications);
        Assert.Empty(BoosterTestData.Store(db).Read(BoosterTestData.Pilot).Boosters);
    }

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return BlueMarshal.Decode(BlueMarshal.Encode(((PilotCallResult.Success)result).Reply));
    }

    private static Dictionary<long, object?> Map(object? value) =>
        value is Dictionary<string, object?> { Count: 0 } ? [] : Assert.IsType<Dictionary<long, object?>>(value);

    private static Dictionary<string, object?> Fields(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State);
}
