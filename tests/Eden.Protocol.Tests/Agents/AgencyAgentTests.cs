using Eden.Data.World.Agents;
using Eden.Gameplay.Agents;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class AgencyAgentTests
{
    private const long EpicAgent = 3020000;
    private const string AgentService = "@agent:3008416";

    [Fact]
    public void FinderEpicAvailabilityIsNativeFalseAndRejectsForgedRequestsWithoutCreatingMissions()
    {
        using MissionFixture f = new(
            live: true,
            configure: catalog =>
            {
                AgentTestData.Add(catalog, EpicAgent, AgentType.EpicArc, "Epic agent");
                catalog.SaveChanges();
            }
        );
        const string method = "AgentHasEpicMissionsForCharacter";
        foreach (long id in new[] { EpicAgent, MissionFixture.AgentId, AgentTestData.OtherAgent })
        {
            Assert.False(Assert.IsType<bool>(MissionFixture.Reply(f.Call("epicArcStatus", method, [id]))));
        }
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [null],
                [true],
                ["3020000"],
                [0L],
                [long.MaxValue],
                [MissionFixture.PilotId],
                [AgentTestData.DeletedAgent],
                [EpicAgent, MissionFixture.PilotId],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call("epicArcStatus", method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(
            f.Call("epicArcStatus", method, [EpicAgent], new() { ["characterID"] = MissionFixture.PilotId + 1 })
        );
        Assert.False(
            Assert.IsType<bool>(MissionFixture.Reply(f.Call("epicArcStatus", method, [EpicAgent], new() { ["machoVersion"] = 1L })))
        );
        Assert.Same(PilotCallResult.Unsupported, f.Call("epicArcStatus", "UnknownMethod", [EpicAgent]));
        Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
        Assert.Empty(f.Context.Notifications);
        f.Context.LogOff();
        Assert.Same(PilotCallResult.Unsupported, f.Call("epicArcStatus", method, [EpicAgent]));
    }

    [Theory]
    [InlineData(MissionRules.SecurityDivision)]
    [InlineData(MissionRules.DistributionDivision)]
    [InlineData(MissionRules.MiningDivision)]
    public async Task CardsBrowseBindConverseAndPreviewCurrentMissionsWithoutChangingThemAcrossCheckpoint(int division)
    {
        using MissionFixture f = new(division: division, live: true);
        var directory = Fields(MissionFixture.Reply(f.Call("agentMgr", "GetAgents", [])));
        var header = Assert.IsType<List<object?>>(directory["header"]).Select(BlueMarshal.Text).ToList();
        var row = Assert
            .IsType<List<object?>>(directory["lines"])
            .Cast<object?[]>()
            .Single(r => Equals(r[header.IndexOf("agentID")], MissionFixture.AgentId));
        Assert.Equal((long)division, row[header.IndexOf("divisionID")]);
        Assert.Equal((long)MissionFixture.StationId, row[header.IndexOf("stationID")]);
        Assert.Equal(30000142L, row[header.IndexOf("solarsystemID")]);
        Assert.False(Assert.IsType<bool>(row[header.IndexOf("isLocatorAgent")]));
        var binding = Assert.IsType<BoundPilotReply>(
            Assert
                .IsType<PilotCallResult.Success>(
                    f.Call("agentMgr", "MachoBindObject", [MissionFixture.AgentId, new object?[] { "DoAction", new object?[] { null } }])
                )
                .Reply
        );
        string service = f.Context.ResolveReference(binding.Reference.Id)!;
        Assert.Null(MissionFixture.Reply(f.Call(service, "GetEntryPoint", [])));
        Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
        Assert.IsType<PilotCallResult.Success>(f.Call(service, "DoAction", [(long)MissionAction.Request]));
        var offered = f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!;
        Assert.Null(MissionFixture.Reply(f.Call(service, "GetEntryPoint", [])));
        AssertJournal(f, service, offered);
        Assert.IsType<PilotCallResult.Success>(f.Call(service, "DoAction", [Token(offered, MissionAction.Accept)]));
        var accepted = f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!;
        Assert.Equal(MissionState.Accepted, accepted.State);
        Assert.Contains(f.Context.Notifications.OfType<AgentMissionChanged>(), n => n.Action == "accepted");
        f.Context.Notifications.Clear();
        byte[] preview = Preview(f, service, accepted);
        using (var metrics = SessionPerformance.Begin("warmed Agency mission previews", _ => { }, force: true)!)
        {
            for (int cycle = 0; cycle < 3; cycle++)
            {
                Assert.Equal(preview, Preview(f, service, accepted));
                AssertJournal(f, service, accepted);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        Assert.Equal(accepted, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId));
        Assert.Empty(f.Context.Notifications);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        Assert.Equal(accepted, missions.Current(MissionFixture.PilotId, MissionFixture.AgentId));
        if (accepted.DungeonId is int dungeon)
        {
            Assert.NotNull(missions.ShipRestrictions(MissionFixture.PilotId, MissionFixture.AgentId, dungeon));
        }
    }

    [Fact]
    public void MissionPreviewRejectsOtherAgentsDungeonsGatesAndMalformedRequestsWithoutSideEffects()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: true);
        Assert.IsType<PilotCallResult.Failure>(f.Call(AgentService, "GetDungeonShipRestrictions", [1L]));
        var offer = f.Accept(f.Request());
        long dungeon = offer.DungeonId!.Value;
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [null],
                [true],
                ["213"],
                [0L],
                [-1L],
                [long.MaxValue],
                [dungeon + 1],
                [dungeon, false],
                [dungeon, 0L],
                [dungeon, -1L],
                [dungeon, long.MaxValue],
                [dungeon, null, null],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call(AgentService, "GetDungeonShipRestrictions", args));
        }
        foreach (string method in new[] { "GetEntryPoint", "GetDungeonShipRestrictions" })
        {
            object?[] args = method == "GetEntryPoint" ? [] : [dungeon];
            Assert.IsType<PilotCallResult.Failure>(f.Call(AgentService, method, args, new() { ["charID"] = MissionFixture.PilotId + 1 }));
            Assert.IsType<PilotCallResult.Failure>(f.Call($"@agent:{MissionFixture.PilotId}", method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call(AgentService, "GetEntryPoint", [MissionFixture.PilotId]));
        Assert.Null(MissionFixture.Reply(f.Call($"@agent:{MissionFixture.StorylineId}", "GetEntryPoint", [])));
        Assert.IsType<PilotCallResult.Failure>(f.Call($"@agent:{MissionFixture.StorylineId}", "GetDungeonShipRestrictions", [dungeon]));
        Assert.Equal(offer, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId));
        Assert.Empty(f.Context.Notifications);
        f.Clock.UtcNow = DateTimeOffset.FromFileTime(offer.ExpiresAt - 1);
        Assert.NotNull(MissionFixture.Reply(f.Call(AgentService, "GetEntryPoint", [])));
        f.Clock.UtcNow = DateTimeOffset.FromFileTime(offer.ExpiresAt);
        Assert.IsType<PilotCallResult.Failure>(f.Call(AgentService, "GetDungeonShipRestrictions", [dungeon]));
        Assert.Null(MissionFixture.Reply(f.Call(AgentService, "GetEntryPoint", [])));
        LiveWorld
            .Find(f.Db.Path)!
            .Economy.Inspect(world =>
            {
                var saved = world.Set<SavedMission>().Find(offer.Id)!;
                Assert.Equal(MissionState.Accepted, saved.State);
                Assert.Null(saved.ClosedAt);
                return true;
            });
        Assert.Empty(f.Context.Notifications);
        f.Context.LogOff();
        Assert.Same(PilotCallResult.Unsupported, f.Call(AgentService, "GetEntryPoint", []));
        Assert.Same(PilotCallResult.Unsupported, f.Call(AgentService, "GetDungeonShipRestrictions", [dungeon]));
    }

    private static long Token(MissionOffer offer, MissionAction action) => offer.Id * MissionRules.ActionStride + (int)action;

    private static Dictionary<string, object?> Fields(object? value) =>
        BlueRead.Fields(value is BlueInstance instance ? instance.State : value);

    private static void AssertJournal(MissionFixture f, string service, MissionOffer offer)
    {
        byte[] global = BlueMarshal.Encode(MissionFixture.Reply(f.Call("agentMgr", "GetMyJournalDetails", [])));
        var journal = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call(service, "GetMyJournalDetails", [])));
        Assert.Equal(global, BlueMarshal.Encode(journal));
        var row = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(journal[0])));
        Assert.Equal((long)offer.State, row[0]);
        Assert.Equal(offer.Storyline, row[1]);
        Assert.Equal(offer.Agent.Id, row[4]);
        Assert.Equal(offer.ExpiresAt, row[5]);
        Assert.Equal((long)offer.MissionId, row[9]);
    }

    private static byte[] Preview(MissionFixture f, string service, MissionOffer offer)
    {
        var entry = MissionFixture.Reply(f.Call(service, "GetEntryPoint", []));
        if (offer.DungeonId is int dungeonId)
        {
            Assert.Equal(new object?[] { offer.Position.X, offer.Position.Y, offer.Position.Z }, Assert.IsType<object?[]>(entry));
            var restrictions = Fields(MissionFixture.Reply(f.Call(service, "GetDungeonShipRestrictions", [(long)dungeonId])));
            Assert.IsType<List<object?>>(restrictions["allowedShipTypes"]);
            Assert.IsType<List<object?>>(restrictions["restrictedShipTypes"]);
            Assert.Equal(offer.HasShipRestrictions, restrictions["nonDefaultShipRestrictions"]);
            Assert.Equal(
                BlueMarshal.Encode(restrictions),
                BlueMarshal.Encode(Fields(MissionFixture.Reply(f.Call(service, "GetDungeonShipRestrictions", [(long)dungeonId, null]))))
            );
        }
        else
        {
            Assert.Null(entry);
        }
        var location = Fields(MissionFixture.Reply(f.Call(service, "GetAgentLocationWrap", [])));
        Assert.Equal((long)offer.Agent.StationId!, location["locationID"]);
        var info = Fields(MissionFixture.Reply(f.Call(service, "GetMissionJournalInfo", [null, null])));
        var objectives = Fields(info["objectives"]);
        var dungeons = Assert.IsType<List<object?>>(objectives["dungeons"]);
        if (offer.DungeonId is not null)
        {
            var dungeon = Fields(Assert.Single(dungeons));
            if (offer.EnemyFactionId > 0)
            {
                Assert.Equal((long)offer.EnemyFactionId, dungeon["ownerID"]);
            }
        }
        else
        {
            Assert.Empty(dungeons);
        }
        return BlueMarshal.Encode(info);
    }
}
