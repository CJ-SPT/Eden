using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PilotServicesTests
{
    [Fact]
    public async Task ListsSameOwnedPilotOnFreshSessions()
    {
        foreach (long sessionId in new long[] { 41, 42 })
        {
            var packets = await ExchangeAsync("charUnboundMgr", "GetCharacterSelectionData", [], sessionId);
            var roster = PilotLogin.Tuple(Result(packets.Single()), 4);
            var character = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(roster[2])));
            var fields = PilotLogin.Fields(character.State);
            Assert.Equal(90000001L, fields["characterID"]);
            Assert.Equal("Eden Pilot", BlueMarshal.Text(fields["characterName"]));
            Assert.Equal(601L, fields["shipTypeID"]);
        }
    }

    [Theory]
    [InlineData(90000001, true)]
    [InlineData(90000002, false)]
    public async Task AcceptsOnlyOwnedCharacter(int characterId, bool expected)
    {
        var packets = await ExchangeAsync("charUnboundMgr", "SelectCharacterID", [characterId, null, false], 42);
        if (!expected)
        {
            Assert.EndsWith(".ErrorResponse", Assert.Single(packets).Name);
            return;
        }

        Assert.Equal(2, packets.Count);
        Assert.EndsWith(".SessionChangeNotification", packets[0].Name);
        var envelope = PilotLogin.Tuple(packets[0].State, 14);
        Assert.Equal(16, envelope[0]);
        var body = PilotLogin.Tuple(envelope[4], 3);
        Assert.Equal(42L, body[0]);
        var delta = PilotLogin.Tuple(body[1], 2);
        var changes = PilotLogin.Fields(delta[1]);
        Assert.Equal(new object?[] { null, 90000001L }, changes["charid"]);
        Assert.Equal(new object?[] { null, 1000000000001L }, changes["shipid"]);
        Assert.Equal(new object?[] { null, 60003760 }, changes["stationid"]);
        Assert.Equal(new object?[] { null, 30000142 }, changes["solarsystemid2"]);
        Assert.DoesNotContain("solarsystemid", changes.Keys);
        Assert.EndsWith(".CallRsp", packets[1].Name);
    }

    [Fact]
    public async Task UnknownMethodsReceiveAnError()
    {
        var packets = await ExchangeAsync("charUnboundMgr", "DeleteCharacter", [90000001], 42);
        Assert.EndsWith(".ErrorResponse", packets.Single().Name);
    }

    private static object? Result(BlueInstance packet)
    {
        var state = PilotLogin.Tuple(packet.State, 14);
        return BlueMarshal.Decode(PilotLogin.Bytes(PilotLogin.Tuple(state[4], 1)[0]));
    }

    private static async Task<List<BlueInstance>> ExchangeAsync(string service, string method, object?[] args, long sessionId)
    {
        const string address = "carbon.common.script.net.machoNetAddress.MachoAddress";
        byte[] call = BlueMarshal.Encode(new object?[] { 1, method, args, new Dictionary<string, object?>() });
        BlueInstance request = new("carbon.common.script.net.machoNetPacket.CallReq", new object?[]
        {
            6L, new BlueInstance(address, new object?[] { 2L, 0L, 1L, null }),
            new BlueInstance(address, new object?[] { 8L, service, null }), null,
            new object?[] { new object?[] { 0L, call } }, null, null, null, null, null, null, null, null, null,
        });
        using TestWorld world = new();
        Queue<object?> input = new([request]);
        List<BlueInstance> output = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() => PilotServices.RunAsync(
            () => input.TryDequeue(out object? packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
            packet =>
            {
                output.Add(Assert.IsType<BlueInstance>(packet));
                return Task.CompletedTask;
            },
            _ => { }, sessionId, world.Store, []));
        return output;
    }
}
