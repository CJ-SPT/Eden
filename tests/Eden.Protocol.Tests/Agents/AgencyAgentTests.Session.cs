using Eden.Gameplay.Agents;
using Eden.Protocol.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Protocol.Tests.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class AgencyAgentTests
{
    [Fact]
    public async Task NativeAgencySessionBindsAndReturnsDirectoryAvailabilityMissionEntranceAndShipLists()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: true);
        var offer = f.Accept(f.Request());
        using var scope = f.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        const string reference = "N=10001:42-2";
        Queue<object?> requests = new([
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [MissionFixture.PilotId]),
            PilotServicesTests.Request("agentMgr", "GetAgents", []),
            PilotServicesTests.Request("epicArcStatus", "AgentHasEpicMissionsForCharacter", [AgentTestData.OtherAgent]),
            PilotServicesTests.Request(
                "agentMgr",
                "MachoBindObject",
                [MissionFixture.AgentId, new object?[] { "GetEntryPoint", Array.Empty<object?>(), new Dictionary<string, object?>() }]
            ),
            PilotServicesTests.Request(null, "GetEntryPoint", [], reference),
            PilotServicesTests.Request(null, "GetDungeonShipRestrictions", [(long)offer.DungeonId!.Value], reference),
            PilotServicesTests.Request(null, "GetMissionJournalInfo", [null, null], reference),
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
        Assert.DoesNotContain(packets, p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var replies = packets.Where(p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(7, replies.Length);
        var directory = Fields(Decode(replies[1]));
        Assert.Contains("isLocatorAgent", Assert.IsType<List<object?>>(directory["header"]).Select(BlueMarshal.Text));
        Assert.False(Assert.IsType<bool>(Decode(replies[2])));
        Assert.Equal(BlueMarshal.Encode(new BoundPilotReply(new(reference, 2), new MissionEntryPoint(offer.Position))), Bytes(replies[3]));
        Assert.Equal(new object?[] { offer.Position.X, offer.Position.Y, offer.Position.Z }, Assert.IsType<object?[]>(Decode(replies[4])));
        Assert.IsType<List<object?>>(Fields(Decode(replies[5]))["restrictedShipTypes"]);
        Assert.Equal((long)offer.MissionId, Fields(Decode(replies[6]))["contentID"]);

        static byte[] Bytes(BlueInstance packet) => BlueRead.Bytes(BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1)[0]);

        static object? Decode(BlueInstance packet) => BlueMarshal.Decode(Bytes(packet));
    }
}
