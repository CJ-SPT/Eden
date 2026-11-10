using Eden.Data.Catalog;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Gameplay.Agents;
using Eden.Protocol.Agents;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class AgentShowInfoTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAgentInfoBindsAndLoadsServiceDetailsWithAdjacentDirectoryReads(bool embedded)
    {
        using MissionFixture fixture = new();
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        const string reference = "N=10001:42-2";
        Queue<object?> requests = new([
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [MissionFixture.PilotId]),
            PilotServicesTests.Request("agentMgr", "GetAgents", []),
            PilotServicesTests.Request(
                "agentMgr",
                "MachoBindObject",
                [
                    MissionFixture.AgentId,
                    embedded ? new object?[] { "GetInfoServiceDetails", Array.Empty<object?>(), new Dictionary<string, object?>() } : null,
                ]
            ),
            PilotServicesTests.Request(null, "GetInfoServiceDetails", [], reference),
            PilotServicesTests.Request("agentMgr", "GetAgentStaticInfo", [MissionFixture.AgentId]),
            PilotServicesTests.Request("agentMgr", "GetAgentByID", [MissionFixture.AgentId]),
            PilotServicesTests.Request("agentMgr", "GetSolarSystemOfAgent", [MissionFixture.AgentId]),
            PilotServicesTests.Request("agentMgr", "GetCompletedCareerAgentIDs", [new List<object?> { AgentTestData.OtherAgent }]),
            PilotServicesTests.Request("agentMgr", "GetMyEpicArcStatus", []),
            PilotServicesTests.Request("agentMgr", "GetMessagesForEpicArcMissions", []),
        ]);
        List<BlueInstance> packets = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                () => requests.TryDequeue(out var next) ? Task.FromResult(next) : throw new EndOfStreamException(),
                packet =>
                {
                    packets.Add(Assert.IsType<BlueInstance>(packet));
                    return Task.CompletedTask;
                },
                _ => { },
                1000001,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        Assert.DoesNotContain(packets, packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var replies = packets.Where(packet => packet.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(10, replies.Length);
        var information = fixture.Missions.Information(MissionFixture.PilotId, MissionFixture.AgentId);
        BlueModel boundReply = embedded
            ? new AgentServiceDetailsReply(information)
            : Assert.IsType<PilotCallResult.Success>(PilotCallResult.None).Reply;
        Assert.Equal(BlueMarshal.Encode(new BoundPilotReply(new(reference, 2), boundReply)), Bytes(replies[2]));
        var details = Fields(Decode(replies[3]));
        Assert.Equal(MissionFixture.AgentId, details["agentID"]);
        Assert.Equal((long)MissionFixture.StationId, details["stationID"]);
        Assert.Equal(1L, details["level"]);
        Assert.Null(details["incompatible"]);
        var service = Fields(Assert.Single(Assert.IsType<List<object?>>(details["services"])));
        Assert.Equal("mission", BlueMarshal.Text(service["agentServiceType"]));
        Assert.True(Assert.IsType<bool>(service["available"]));
        Assert.Equal(Bytes(replies[4]), Bytes(replies[5]));
        var agent = Fields(Decode(replies[4]));
        Assert.Equal(MissionFixture.AgentId, agent["agentID"]);
        Assert.Equal((long)MissionRules.DistributionDivision, agent["divisionID"]);
        Assert.Equal(30000142L, agent["solarSystemID"]);
        Assert.Equal(30000142L, Decode(replies[6]));
        var completed = Assert.IsType<Dictionary<long, object?>>(Decode(replies[7]));
        Assert.False(Assert.IsType<bool>(completed[AgentTestData.OtherAgent]));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(Decode(replies[8])));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(Decode(replies[9])));
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Empty(saved.Set<SavedMission>());
        Assert.Empty(saved.Set<SavedAgentDecline>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AgentInfoReadsCurrentAccessAndActiveDeadlinesWithoutChangingOffersIncludingAfterReopen(bool live)
    {
        using MissionFixture fixture = new(level: 2, live: live);
        var denied = fixture.Missions.Information(MissionFixture.PilotId, MissionFixture.AgentId);
        Assert.False(denied.MissionAvailable);
        Assert.Contains("standing", denied.Incompatible);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = AgentTestData.Corporation,
                    Value = 2,
                }
            );
            world.SaveChanges();
        }
        Assert.True(fixture.Missions.Information(MissionFixture.PilotId, MissionFixture.AgentId).MissionAvailable);
        var offer = fixture.Request();
        Assert.False(fixture.Missions.Information(MissionFixture.PilotId, MissionFixture.AgentId).MissionAvailable);
        fixture.Clock.UtcNow = new DateTimeOffset(DateTime.FromFileTimeUtc(offer.ExpiresAt));
        Assert.True(fixture.Missions.Information(MissionFixture.PilotId, MissionFixture.AgentId).MissionAvailable);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        Assert.True(fixture.Missions.Information(MissionFixture.PilotId, MissionFixture.AgentId).MissionAvailable);
        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock);
        Assert.True(
            reopened.GetRequiredService<IPilotMissions>().Information(MissionFixture.PilotId, MissionFixture.AgentId).MissionAvailable
        );
        using WorldDbContext saved = new(fixture.Db.Path);
        var row = saved.Set<SavedMission>().Find(offer.Id)!;
        Assert.Equal(MissionState.Offered, row.State);
        Assert.Equal(offer.ExpiresAt, row.ExpiresAt);
        Assert.Null(row.ClosedAt);
        Assert.Empty(fixture.Context.Notifications);
    }

    [Fact]
    public void SpecialAndSpaceAgentsRemainInspectableAndUnknownLocationsPreserveNone()
    {
        using MissionFixture fixture = new(configure: catalog =>
        {
            AgentTestData.Add(catalog, 3020000, AgentType.Research, "Space researcher", station: null);
            catalog.SaveChanges();
        });
        var details = Fields(MissionFixture.Reply(fixture.Call("@agent:3020000", "GetInfoServiceDetails", [])));
        Assert.Null(details["stationID"]);
        Assert.Equal(3L, details["level"]);
        Assert.Empty(Assert.IsType<List<object?>>(details["services"]));
        Assert.NotEmpty(BlueMarshal.Text(details["incompatible"]));
        Assert.Null(MissionFixture.Reply(fixture.Call("agentMgr", "GetSolarSystemOfAgent", [3020000L])));
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Empty(saved.Set<SavedMission>());
        Assert.Empty(fixture.Context.Notifications);
    }

    [Fact]
    public void InformationRejectionsDoNotExpireOffersOrAcceptForgedCharacterArguments()
    {
        using MissionFixture fixture = new();
        using var scope = fixture.Services.CreateScope();
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        using PilotCallContext unselected = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            43,
            SignedSettings.Empty
        );
        Assert.Same(
            PilotCallResult.Unsupported,
            scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(unselected, $"@agent:{MissionFixture.AgentId}", "GetInfoServiceDetails", Array.Empty<object?>(), _ => { })
        );
        var offer = fixture.Request();
        fixture.Clock.AdvanceWallTime(MissionRules.OfferLifetime + TimeSpan.FromSeconds(1));
        string service = $"@agent:{MissionFixture.AgentId}";
        foreach (object?[] args in new object?[][] { [MissionFixture.PilotId], [null], [true] })
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, "GetInfoServiceDetails", args));
        }
        Assert.IsType<PilotCallResult.Failure>(
            fixture.Call(service, "GetInfoServiceDetails", [], new() { ["charID"] = MissionFixture.PilotId + 1 })
        );
        Assert.IsType<PilotCallResult.Failure>(fixture.Call($"@agent:{AgentTestData.NonAgent}", "GetInfoServiceDetails", []));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call($"@agent:{AgentTestData.DeletedAgent}", "GetInfoServiceDetails", []));
        using WorldDbContext saved = new(fixture.Db.Path);
        var row = Assert.Single(saved.Set<SavedMission>());
        Assert.Equal(offer.ExpiresAt, row.ExpiresAt);
        Assert.Equal(MissionState.Offered, row.State);
        Assert.Null(row.ClosedAt);
        Assert.Empty(fixture.Context.Notifications);
    }

    [Fact]
    public void StorylineInformationReadsOnlyTheSelectedPilotsUnexpiredOffer()
    {
        using MissionFixture fixture = new();
        Assert.Contains(
            "no active storyline",
            fixture.Missions.Information(MissionFixture.PilotId, MissionFixture.StorylineId).Incompatible
        );
        var offer = fixture.Request();
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            var row = world.Set<SavedMission>().Find(offer.Id)!;
            row.AgentId = MissionFixture.StorylineId;
            row.Storyline = true;
            world.SaveChanges();
        }
        var own = fixture.Missions.Information(MissionFixture.PilotId, MissionFixture.StorylineId);
        Assert.Empty(own.Incompatible);
        Assert.False(own.MissionAvailable);
        Assert.Contains("no active storyline", fixture.Missions.Information(90000002, MissionFixture.StorylineId).Incompatible);
        fixture.Clock.AdvanceWallTime(MissionRules.OfferLifetime + TimeSpan.FromSeconds(1));
        Assert.Contains(
            "no active storyline",
            fixture.Missions.Information(MissionFixture.PilotId, MissionFixture.StorylineId).Incompatible
        );
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Null(saved.Set<SavedMission>().Find(offer.Id)!.ClosedAt);
    }

    private static byte[] Bytes(BlueInstance packet) => BlueRead.Bytes(BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1)[0]);

    private static object? Decode(BlueInstance packet) => BlueMarshal.Decode(Bytes(packet));

    private static Dictionary<string, object?> Fields(object? reply) => BlueRead.Fields(Assert.IsType<BlueInstance>(reply).State);
}
