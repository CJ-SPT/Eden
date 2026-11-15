using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Protocol.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Runtime;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class AgencyAgentTests
{
    [Fact]
    public void StorylineCardsUseEarnedPersonalJournalOffersAndNativeRefreshNotifications()
    {
        using MissionFixture f = new(live: true);
        var before = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call("agentMgr", "GetMyJournalDetails", [])));
        Assert.Empty(Assert.IsType<List<object?>>(before[0]));
        var live = LiveWorld.Find(f.Db.Path)!;
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Add(
                new SavedMissionProgress
                {
                    PilotId = MissionFixture.PilotId,
                    FactionId = 500001,
                    Level = 1,
                    Completed = MissionRules.StorylineInterval - 1,
                }
            );
            action.Commit();
        }
        var courier = f.Accept(f.Request());
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            var cargo = action.Set<OwnedItem>().Single(i => i.TypeId == courier.Cargo!.TypeId);
            cargo.LocationId = courier.Cargo!.DestinationStation;
            action.Pilots.Find(MissionFixture.PilotId)!.ActiveShip.StationId = courier.Cargo.DestinationStation;
            action.Commit();
        }
        Assert.IsType<PilotCallResult.Success>(f.Call(AgentService, "DoAction", [Token(courier, MissionAction.Complete)]));
        var storyline = Assert.Single(f.Missions.Journal(MissionFixture.PilotId));
        Assert.True(storyline.Storyline);
        Assert.Equal(MissionFixture.StorylineId, storyline.Agent.Id);
        Assert.Equal(MissionState.Offered, storyline.State);
        Assert.Contains(
            f.Context.Notifications.OfType<AgentMissionChanged>(),
            n => n.Action == "offered" && n.AgentId == MissionFixture.StorylineId
        );
        Assert.Contains(
            f.Context.Notifications.OfType<AgentMissionChanged>(),
            n => n.Action == "completed" && n.AgentId == MissionFixture.AgentId
        );
        string service = $"@agent:{MissionFixture.StorylineId}";
        AssertJournal(f, service, storyline);
        var journal = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call(service, "GetMyJournalDetails", [])));
        var row = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(journal[0])));
        Assert.Equal("UI/Agents/MissionTypes/StorylineCourier", BlueMarshal.Text(row[2]));
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Pilots.Find(MissionFixture.PilotId)!.ActiveShip.StationId = MissionFixture.StationId;
            action.Commit();
        }
        Assert.IsType<PilotCallResult.Success>(f.Call(service, "DoAction", [Token(storyline, MissionAction.Accept)]));
        Assert.Equal(MissionState.Accepted, f.Missions.Current(MissionFixture.PilotId, storyline.Agent.Id)!.State);
        Assert.DoesNotContain(f.Missions.Journal(MissionFixture.PilotId + 1), m => m.Agent.Id == storyline.Agent.Id);
    }
}
