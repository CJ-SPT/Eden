using Eden.Gameplay.Contracts;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class ContractSearchSessionTests
{
    [Fact]
    public async Task ProxyAddressedSearchReachesTheHandlerAndReturnsRenderableCurrentRegionResults()
    {
        using TestWorld world = new();
        ContractTests.Fund(world, ContractAssetTestData.Pilot);
        long item = ContractAssetTestData.Stack(world);
        long id = world
            .Services.GetRequiredService<IPilotContracts>()
            .Create(ContractAssetTestData.Pilot, ContractTests.Draft(item, assignee: null));
        var search = PilotServicesTests.Request(
            "contractProxy",
            "SearchContracts",
            [],
            keywords: new()
            {
                ["contractType"] = 10L,
                ["locationID"] = 10000002L,
                ["availability"] = 0L,
                ["sortBy"] = 1L,
                ["sortDir"] = 0L,
                ["startNum"] = 0L,
            }
        );
        // sm.ProxySvc explicitly addresses the proxy node, rather than an ANY
        // service address. Exercise the actual destination used by contract search.
        var fields = Assert.IsType<object?[]>(search.State);
        fields[2] = new BlueInstance(
            "carbon.common.script.net.machoNetAddress.MachoAddress",
            new object?[] { 1L, (long)PilotLogin.NodeId, "contractProxy", null }
        );
        Queue<object?> input = new(
            new[] { PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [ContractAssetTestData.Pilot]), search }.Select(
                packet => BlueMarshal.Decode(BlueMarshal.Encode(packet))
            )
        );
        List<BlueInstance> output = [];
        List<string> reports = [];
        using var scope = world.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                () => input.TryDequeue(out var packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
                packet =>
                {
                    output.Add(Assert.IsType<BlueInstance>(packet));
                    return Task.CompletedTask;
                },
                reports.Add,
                1000001,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        Assert.Contains("Call contractProxy.SearchContracts.", reports);
        Assert.DoesNotContain(output, packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var reply = output.Last(packet => packet.Name.EndsWith(".CallRsp", StringComparison.Ordinal));
        var body = BlueRead.Tuple(BlueRead.Tuple(reply.State, 14)[4], 1);
        var result = BlueRead.Fields(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueRead.Bytes(body[0]))).State);
        Assert.Equal(1L, result["numFound"]);
        var bundle = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(result["contracts"]))).State);
        var contract = BlueRead.Fields(Assert.IsType<BlueInstance>(bundle["contract"]).State);
        Assert.Equal(id, contract["contractID"]);
        Assert.Equal(30000142L, contract["startSolarSystemID"]);
        Assert.Equal(30000142L, contract["endSolarSystemID"]);
        // RenderPage calls IsStationInaccessible for both endpoints. The pinned helper returns false
        // for None; integer zero instead triggers a remote structure-service probe and hides the row.
        Assert.Null(contract["endStationID"]);
    }
}
