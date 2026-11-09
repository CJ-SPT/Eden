using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Protocol.Agents;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionProtocolTests
{
    [Fact]
    public void StationAgentPanelCanBindAndReadReplayTimersBeforeAnyMissionOfferExists()
    {
        using MissionFixture f = new();
        foreach (long agent in new[] { MissionFixture.AgentId, MissionFixture.StorylineId, Persistence.AgentTestData.OtherAgent })
        {
            var bound = Assert.IsType<BoundPilotReply>(
                Assert
                    .IsType<PilotCallResult.Success>(
                        f.Call(
                            "agentMgr",
                            "MachoBindObject",
                            [agent, new object?[] { "GetReplayTimestamp", new object?[] { MissionFixture.PilotId } }]
                        )
                    )
                    .Reply
            );
            Assert.Null(MissionFixture.Reply(new PilotCallResult.Success(bound.Reply)));
            string service = f.Context.ResolveReference(bound.Reference.Id)!;
            Assert.Null(MissionFixture.Reply(f.Call(service, "GetReplayTimestamp", [MissionFixture.PilotId])));
            Assert.IsType<PilotCallResult.Failure>(f.Call(service, "GetReplayTimestamp", [MissionFixture.PilotId + 1]));
        }
        Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
    }

    [Fact]
    public void BoundAgentRequestAcceptanceJournalAndNativeObjectiveShapesUseCatalogIds()
    {
        using MissionFixture f = new();
        var binding = Assert.IsType<BoundPilotReply>(
            Assert
                .IsType<PilotCallResult.Success>(
                    f.Call("agentMgr", "MachoBindObject", [MissionFixture.AgentId, new object?[] { "DoAction", new object?[] { null } }])
                )
                .Reply
        );
        string service = f.Context.ResolveReference(binding.Reference.Id)!;
        Assert.Equal($"@agent:{MissionFixture.AgentId}", service);
        var requested = Conversation(f.Call(service, "DoAction", [(long)MissionAction.Request]));
        var message = Assert.IsType<object?[]>(requested.Message);
        Assert.Equal(2912L, message[1]);
        var accept = Assert.IsType<object?[]>(requested.Actions[0]);
        Assert.Equal((long)MissionAction.Accept, accept[1]);
        Assert.Equal(35L, accept[0]);
        Assert.Equal(true, requested.Last["missionOffered"]);
        _ = Conversation(f.Call(service, "DoAction", [accept[0]]));
        Assert.Contains(f.Context.Notifications, n => n is AgentMissionChanged { Action: "accepted" });
        var journal = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call("agentMgr", "GetMyJournalDetails", [])));
        var row = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(journal[0])));
        Assert.Equal(10, row.Length);
        Assert.Equal((long)MissionState.Accepted, row[0]);
        Assert.Equal(MissionFixture.AgentId, row[4]);
        Assert.Equal(2912L, row[9]);
        var objective = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [])));
        Assert.Equal(2912L, objective["contentID"]);
        Assert.Equal(1000L, objective["loyaltyPoints"]);
        var rewards = Assert.IsType<List<object?>>(objective["normalRewards"]);
        var isk = Assert.IsType<object?[]>(Assert.Single(rewards));
        Assert.Equal(29L, isk[0]);
        Assert.Equal(200000d, isk[1]);
        var transport = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(objective["objectives"])));
        Assert.Equal("transport", BlueMarshal.Text(transport[0]));
        Assert.Equal(5, Assert.IsType<object?[]>(transport[1]).Length);
        var briefing = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionBriefingInfo", [])));
        Assert.Equal(2912L, briefing["ContentID"]);
        Assert.NotNull(briefing["AcceptTimestamp"]);
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "GetMissionKeywords", [2913L]));
    }

    [Fact]
    public void CompletionNotifiesWalletLpStandingsAndJournalOnlyAfterCommittedSettlement()
    {
        using MissionFixture f = new();
        var offer = f.Accept(f.Request());
        _ = f.Context;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var cargo = world.Set<OwnedItem>().Single(i => i.TypeId == offer.Cargo!.TypeId);
            cargo.LocationId = MissionFixture.DestinationId;
            world.SaveChanges();
        }
        f.Dock(MissionFixture.DestinationId);
        string service = $"@agent:{MissionFixture.AgentId}";
        long token = offer.Id * MissionRules.ActionStride + (int)MissionAction.Complete;
        var complete = Conversation(f.Call(service, "DoAction", [token]));
        Assert.Equal(true, complete.Last["missionCompleted"]);
        Assert.Equal(2, f.Context.Notifications.OfType<NpcStandingSet>().Count());
        Assert.Contains(f.Context.Notifications, n => n.Name == "OnAccountChange");
        Assert.Contains(f.Context.Notifications, n => n.Name == "OnLPChange");
        Assert.Contains(f.Context.Notifications, n => n is AgentMissionChanged { Action: "completed" });
        int notifications = f.Context.Notifications.Count;
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [token]));
        Assert.Equal(notifications, f.Context.Notifications.Count);
    }

    [Fact]
    public void ForgedBindingActionsCharacterFiltersAndKeywordsAreRejected()
    {
        using MissionFixture f = new();
        Assert.IsType<PilotCallResult.Failure>(f.Call("agentMgr", "MachoBindObject", [MissionFixture.PilotId, null]));
        Assert.IsType<PilotCallResult.Failure>(f.Call("agentMgr", "MachoBindObject", [new object?[] { MissionFixture.AgentId }, null]));
        string service = $"@agent:{MissionFixture.AgentId}";
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [(long)MissionAction.Accept]));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [-1L]));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [2L], new() { ["reward"] = 1_000_000_000L }));
        var offer = f.Request();
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "GetMissionJournalInfo", [MissionFixture.PilotId + 1, offer.MissionId]));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "GetMissionObjectiveInfo", [MissionFixture.PilotId + 1]));
        Assert.IsType<PilotCallResult.Failure>(f.Call($"@agent:{MissionFixture.PilotId}", "DoAction", [null]));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [offer.Id * MissionRules.ActionStride + 31]));
    }

    [Fact]
    public void CombatBookmarkAndObjectiveExposeNativeMissionWarpWithoutGrantingWarpForAnOffer()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var offer = f.Request();
        string service = $"@agent:{MissionFixture.AgentId}";
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "WarpToLocation", ["dungeon", 0L, 0d, false, null, null]));
        f.Accept(offer);
        var journal = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call("agentMgr", "GetMyJournalDetails", [])));
        var row = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(journal[0])));
        var bookmark = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(row[6])));
        var fields = BlueRead.Fields(bookmark.State);
        Assert.Equal(MissionFixture.AgentId, fields["agentID"]);
        Assert.Equal("dungeon", BlueMarshal.Text(fields["locationType"]));
        var objectives = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [])));
        var dungeon = BlueRead.Fields(Assert.Single(Assert.IsType<List<object?>>(objectives["dungeons"])));
        Assert.Equal(188L, dungeon["dungeonID"]);
        Assert.Null(dungeon["objectiveCompleted"]);
        Assert.Equal(30000142L, BlueRead.Fields(dungeon["location"])["solarsystemID"]);
        // Docking rejects the actual warp through the ordinary flight owner.
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "WarpToLocation", ["dungeon", 0L, 0d, false, null, null]));
        Assert.IsType<PilotCallResult.Success>(
            f.Context.Flight.Execute(new Eden.Gameplay.Flight.FlightCommand.Undock(f.Context.Pilot.ShipId))
        );
        f.Context.Flight.Snapshot();
        Assert.IsType<PilotCallResult.Success>(f.Call(service, "WarpToLocation", ["dungeon", 0L, 0d, false, null, null]));
    }

    private static (object? Message, List<object?> Actions, Dictionary<string, object?> Last) Conversation(PilotCallResult result)
    {
        var reply = Assert.IsType<object?[]>(MissionFixture.Reply(result));
        var dialogue = Assert.IsType<object?[]>(reply[0]);
        return (dialogue[0], Assert.IsType<List<object?>>(dialogue[1]), BlueRead.Fields(reply[1]));
    }
}
