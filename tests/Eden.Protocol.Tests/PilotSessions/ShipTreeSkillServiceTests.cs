using Eden.Gameplay.Skills;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class ShipTreeSkillServiceTests
{
    [Fact]
    public void ShipTreeRecentSkillCallReturnsAnEmptyListWithoutChangingCurrentSkillsOrHistory()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        using LiveWorld live = new(new(db.Path), clock, _ => { });
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        Assert.Same(PilotCallResult.Unsupported, client.Call("GetSkillChangesForISIS", [], "@skills"));
        client.Refresh();
        var training = FreePointTestData.Training(db);
        training.Save(FreePointTestData.Pilot, [new(3327, 1)], true);
        var reader = db.Services.GetRequiredService<IPilotSkillReader>();
        var beforeSkills = reader.ReadSkills(FreePointTestData.Pilot).ToArray();
        var beforeHistory = training.History(FreePointTestData.Pilot, int.MaxValue).ToArray();
        client.Context.Notifications.Clear();

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var reply = Assert.IsType<PilotCallResult.Success>(client.Call("GetSkillChangesForISIS", [], "@skills"));
            var wire = BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply));
            Assert.Empty(Assert.IsType<List<object?>>(wire));
        }

        Assert.Equal(beforeSkills, reader.ReadSkills(FreePointTestData.Pilot));
        Assert.Equal(beforeHistory, training.History(FreePointTestData.Pilot, int.MaxValue));
        Assert.Empty(client.Context.Notifications);
    }

    [Fact]
    public void ShipTreeRecentSkillCallRejectsUnexpectedArgumentsAndKeywords()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        using LiveWorld live = new(new(db.Path), clock, _ => { });
        using CourierServiceTests.Client client = new(db, clock, RedemptionTests.Account);
        client.Refresh();
        Assert.Same(PilotCallResult.Unsupported, client.Call("GetSkillChangesForISIS", [90000002L], "@skills"));
        Assert.Same(PilotCallResult.Unsupported, client.Call("GetSkillChangesForISIS", [], "@skills", new() { ["pilotID"] = 90000002L }));
    }
}
