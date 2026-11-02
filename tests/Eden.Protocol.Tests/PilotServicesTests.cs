namespace Eden.Protocol.Tests;

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
        Assert.EndsWith(expected ? ".CallRsp" : ".ErrorResponse", packets.Single().Name);
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
        Queue<object?> input = new([request]);
        List<BlueInstance> output = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() => PilotServices.RunAsync(
            () => input.TryDequeue(out object? packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
            packet =>
            {
                output.Add(Assert.IsType<BlueInstance>(packet));
                return Task.CompletedTask;
            },
            _ => { }, sessionId));
        return output;
    }
}
