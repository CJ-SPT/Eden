using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class ImplantServiceTests
{
    [Fact]
    public void NativeInstallReadAndUnplugUpdateInventoryAttributesAndTheExistingSkillCache()
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock);
        long item = ImplantTestData.Item(db);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        Assert.Same(PilotCallResult.Unsupported, client.Call("GetImplants", [], "@skills"));
        client.Refresh();
        Assert.Empty(Map(Reply(client.Call("GetImplants", [], "@skills"))));
        client.Context.Notifications.Clear();
        Assert.Null(Reply(client.Call("InjectImplant", [item], "dogmaIM")));
        Assert.Single(client.Context.Notifications.OfType<ItemChanged>());
        var notification = Assert.Single(client.Context.Notifications.OfType<ServerImplantsChanged>());
        Assert.Empty(notification.Arguments);
        Assert.Single(client.Context.Notifications.OfType<SkillQueueSaved>());
        var attributes = Assert.Single(client.Context.Notifications.OfType<ModuleAttributesChanged>()).Changes;
        Assert.Equal(23, Assert.Single(attributes, a => a.Attribute == DogmaAttributeId.Perception).Value);
        var implants = Map(Reply(client.Call("GetImplants", [], "@skills")));
        var implant = Fields(Assert.Single(implants).Value);
        Assert.Equal(item, implant["itemID"]);
        Assert.Equal((long)ImplantTestData.BasicOcular, implant["typeID"]);
        Assert.Equal(23L, Map(Reply(client.Call("GetAttributes", [], "@skills")))[(long)DogmaAttributeId.Perception]);
        using (var scope = db.Services.CreateScope())
        {
            var dogma = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimePilot(client.Context.Pilot);
            Assert.Equal(23, dogma.Attributes[DogmaAttributeId.Perception]);
        }
        client.Context.Notifications.Clear();
        Assert.Null(Reply(client.Call("DestroyImplant", [item], "dogmaIM")));
        Assert.Empty(Map(Reply(client.Call("GetImplants", [], "@skills"))));
        Assert.Equal(20L, Map(Reply(client.Call("GetAttributes", [], "@skills")))[(long)DogmaAttributeId.Perception]);
        Assert.Single(client.Context.Notifications.OfType<ServerImplantsChanged>());
        Assert.DoesNotContain(client.Context.Notifications, n => n.Name == "OnItemChange");
        Assert.IsType<PilotCallResult.Failure>(client.Call("DestroyImplant", [item], "dogmaIM"));
    }

    [Fact]
    public void AnotherSessionSeesNetZeroChangesAndRebasedTrainingThroughPollingAndExplicitReads()
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock);
        var initial = ImplantTestData.Training(db).Save(ImplantTestData.Pilot, [new(3327, 1)], true);
        using CourierServiceTests.Client writer = new(db, clock, 1000001);
        using CourierServiceTests.Client observer = new(db, clock, 1000001);
        writer.Refresh();
        observer.Refresh();
        observer.Context.Notifications.Clear();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(100));
        long item = ImplantTestData.Item(db);
        Assert.Null(Reply(writer.Call("InjectImplant", [item], "dogmaIM")));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(100));
        Assert.Null(Reply(writer.Call("DestroyImplant", [item], "dogmaIM")));
        observer.Advance();
        Assert.Single(observer.Context.Notifications.OfType<ServerImplantsChanged>());
        Assert.Single(observer.Context.Notifications.OfType<ServerSkillsChanged>());
        var end = Assert.Single(ImplantTestData.Training(db).Advance(ImplantTestData.Pilot).Queue).EndTime;
        Assert.Equal(end, observer.Context.NextSkillCompletion);
        Assert.True(end < initial.Queue[0].EndTime);
        observer.Context.Notifications.Clear();
        observer.Advance();
        Assert.Empty(observer.Context.Notifications);
        long next = ImplantTestData.Item(db);
        Assert.Null(Reply(writer.Call("InjectImplant", [next], "dogmaIM")));
        // A native forced refresh must observe a change even inside the poll interval.
        Assert.Single(Map(Reply(observer.Call("GetImplants", [], "@skills"))));
        Assert.Single(observer.Context.Notifications.OfType<ServerImplantsChanged>());
        observer.Context.Notifications.Clear();
        observer.Call("GetImplants", [], "@skills");
        Assert.Empty(observer.Context.Notifications);
    }

    [Fact]
    public void NativeCloneStateReturnsStoredImplantsAndCloneJumpsRefreshActiveBonuses()
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock, clones: true);
        long item = ImplantTestData.Item(db);
        ImplantTestData.Store(db).Install(ImplantTestData.Pilot, item);
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        clones.Install(ImplantTestData.Pilot);
        var body = Assert.Single(clones.Read(ImplantTestData.Pilot).Clones);
        HomeStationTestData.MakeActiveCapsule(db, HomeStationTestData.Origin);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        client.Context.Notifications.Clear();
        Assert.Null(Reply(client.Call("CloneJump", [body.StationId, body.Id, 900000d, false], "jumpCloneSvc")));
        Assert.Single(client.Context.Notifications.OfType<ServerImplantsChanged>());
        Assert.Empty(Map(Reply(client.Call("GetImplants", [], "@skills"))));
        var state = Fields(Reply(client.Call("GetCloneState", [], "jumpCloneSvc")));
        var implants = Fields(state["implants"]);
        Assert.Equal(new[] { "jumpCloneID", "typeID" }, Assert.IsType<List<object?>>(implants["header"]).Select(BlueMarshal.Text));
        var row = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(implants["lines"])));
        var stored = Assert.Single(clones.Read(ImplantTestData.Pilot).Implants);
        Assert.Equal(stored.CloneId, row[0]);
        Assert.Equal((long)ImplantTestData.BasicOcular, row[1]);
        client.Context.Notifications.Clear();
        Assert.Null(Reply(client.Call("DestroyInstalledClone", [stored.CloneId], "jumpCloneSvc")));
        Assert.Empty(clones.Read(ImplantTestData.Pilot).Implants);
        Assert.Equal(CharacterAttributes.Default, ImplantTestData.Store(db).Read(ImplantTestData.Pilot).Attributes);
    }

    [Theory]
    [InlineData("@skills", "GetImplants")]
    [InlineData("dogmaIM", "InjectImplant")]
    [InlineData("dogmaIM", "DestroyImplant")]
    public void MalformedNativeRequestsFailWithoutMutatingThePilot(string service, string method)
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        client.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(client.Call(method, ["invalid"], service));
        Assert.IsType<PilotCallResult.Failure>(client.Call(method, [], service, new() { ["unexpected"] = true }));
        Assert.Empty(ImplantTestData.Store(db).Read(ImplantTestData.Pilot).Implants);
        Assert.Empty(client.Context.Notifications);
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
