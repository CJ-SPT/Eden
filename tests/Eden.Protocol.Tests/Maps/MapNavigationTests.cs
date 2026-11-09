using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Protocol.Tests.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Maps;

public sealed class MapNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryHasStableCacheVersionAndNativeRowsWithoutChangingPilot(bool inSpace)
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var saved = world.Store.FindPilot(1000001)!;
        var pilot = inSpace ? saved with { Position = new SpacePosition(1_000_000, 0, 0) } : saved;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Same(PilotCallResult.Unsupported, Call([3L, 24L]));
        context.Select(pilot);
        byte[]? baseline = null;
        foreach (long statistic in new long[] { 1, 3, 5 })
        {
            foreach (long hours in new long[] { 1, 24 })
            {
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    var reply = Assert.IsType<PilotCallResult.Success>(Call([statistic, hours], new() { ["machoVersion"] = 1L }));
                    byte[] bytes = BlueMarshal.Encode(reply.Reply);
                    AssertHistory(BlueMarshal.Decode(bytes));
                    baseline ??= bytes;
                    Assert.Equal(baseline, bytes);
                }
            }
        }
        foreach (
            object?[] invalid in new object?[][]
            {
                [],
                [3L],
                [3L, 24L, 1L],
                [null, 24L],
                [true, 24L],
                ["3", 24L],
                [0L, 24L],
                [2L, 24L],
                [long.MaxValue, 24L],
                [long.MinValue, 24L],
                [3L, 0L],
                [3L, -1L],
                [3L, long.MaxValue],
                [3L, true],
                [3L, null],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(Call(invalid));
        }
        Assert.IsType<PilotCallResult.Failure>(Call([3L, 24L], new() { ["characterID"] = 90000002L }));
        Assert.Same(PilotCallResult.Unsupported, router.Call(context, "map", "UnknownMapMethod", Array.Empty<object?>(), _ => { }));
        Assert.Equal(pilot, context.Pilot);
        Assert.Equal(saved, world.Store.FindPilot(1000001));
        Assert.Empty(context.Notifications);
        Assert.Empty(context.NotificationsBeforeFlight);

        PilotCallResult Call(object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, "map", "GetHistory", BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeMapSearchAndRoutePreparationPacketsHaveResolvableResults(bool inSpace)
    {
        using TestWorld world = new(clock: CalendarTestData.Clock());
        if (inSpace)
        {
            world.Store.SetPosition(world.Store.FindPilot(1000001)!.ShipId, new SpacePosition(1_000_000, 0, 0));
        }
        var before = world.Store.FindPilot(1000001)!;
        object?[] requests =
        [
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            PilotServicesTests.Request("map", "GetStationInfo", []),
            PilotServicesTests.Request("structureDirectory", "GetJumpBridgesWithMyAccess", []),
            PilotServicesTests.Request("charMgr", "GetHomeStationRow", []),
            PilotServicesTests.Request("map", "GetAllRoamingWeatherSystems", []),
            PilotServicesTests.Request("map", "GetHistory", [3L, 24L]),
            PilotServicesTests.Request("map", "GetHistory", [3L, 24L]),
            PilotServicesTests.Request("search", "Query", ["Jita", new List<object?> { 6L, 7L, 8L, 9L }]),
            PilotServicesTests.Request("structureDirectory", "GetStructureMapData", [30000142L]),
            PilotServicesTests.Request("stationSvc", "GetStation", [60003760L]),
        ];
        Queue<object?> input = new(requests.Select(packet => BlueMarshal.Decode(BlueMarshal.Encode(packet))));
        List<BlueInstance> output = [];
        using var scope = world.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                () => input.TryDequeue(out var packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
                packet =>
                {
                    output.Add(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(packet))));
                    return Task.CompletedTask;
                },
                _ => { },
                1000001,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        Assert.DoesNotContain(output, packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var replies = output
            .Where(packet => packet.Name.EndsWith(".CallRsp", StringComparison.Ordinal))
            .Select(packet =>
            {
                var body = BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1);
                return BlueMarshal.Decode(BlueRead.Bytes(body[0]));
            })
            .ToArray();
        Assert.Equal(requests.Length, replies.Length);
        var stations = BlueRead.Fields(Assert.IsType<BlueInstance>(replies[1]).State);
        Assert.Contains("solarSystemID", Assert.IsType<List<object?>>(stations["header"]).Select(BlueMarshal.Text));
        Assert.NotEmpty(Assert.IsType<List<object?>>(stations["lines"]));
        AssertHistory(replies[5]);
        AssertHistory(replies[6]);
        Assert.Equal(BlueMarshal.Encode(replies[5]), BlueMarshal.Encode(replies[6]));
        var groups = Assert.IsType<Dictionary<long, object?>>(replies[7]);
        Assert.Equal(new List<object?> { 30000142L }, groups[7]);
        Assert.Contains(60003760L, Assert.IsType<List<object?>>(groups[9]));
        Assert.Empty(Assert.IsType<List<object?>>(replies[8]));
        var station = BlueRead.Fields(Assert.IsType<BlueInstance>(replies[9]).State);
        Assert.Equal(60003760L, station["stationID"]);
        Assert.Equal(30000142L, station["solarSystemID"]);
        Assert.Equal(before, world.Store.FindPilot(1000001));
    }

    private static void AssertHistory(object? reply)
    {
        var cached = Assert.IsType<BlueInstance>(reply);
        Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", cached.Name);
        var state = BlueRead.Tuple(cached.State, 3);
        var details = BlueRead.Fields(state[0]);
        Assert.Equal("run", BlueMarshal.Text(details["versionCheck"]));
        Assert.False(details.ContainsKey("sessionInfo"));
        Assert.Equal(new object?[] { 1L, 0L }, BlueRead.Tuple(state[2], 2));
        var rows = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueRead.Bytes(state[1])));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", rows.Name);
        var fields = BlueRead.Fields(rows.State);
        Assert.Equal(
            new[] { "solarSystemID", "value1", "value2", "value3" },
            Assert.IsType<List<object?>>(fields["header"]).Select(BlueMarshal.Text)
        );
        Assert.Empty(Assert.IsType<List<object?>>(fields["lines"]));
    }
}
