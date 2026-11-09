using Eden.Gameplay.Characters;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Maps;

public sealed class MapStartupTests
{
    private static readonly (string Service, string Method)[] StartupCalls =
    [
        ("structureDirectory", "GetJumpBridgesWithMyAccess"),
        ("stationSvc", "GetAllianceSystems"),
        ("fwWarzoneSolarsystem", "GetAllWarzonesOccupationStates"),
        ("fwWarzoneSolarsystem", "GetAllWarzonesOccupationStatesUncached"),
        ("charMgr", "GetHomeStationRow"),
        ("solarsystemInterferenceMgr", "GetAllInterferenceBands"),
        ("solarsystemInterferenceMgr", "GetLocalInterferenceState"),
        ("map", "GetAllRoamingWeatherSystems"),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MapStartupPreservesNativeContainerShapesAndSelectionGating(bool inSpace)
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var savedPilot = world.Store.FindPilot(1000001)!;
        var home = world.Services.GetRequiredService<IPilotHomeStation>().Read(savedPilot.PilotId);
        var pilot = savedPilot;
        if (inSpace)
        {
            pilot = pilot with { Position = new SpacePosition(1_000_000, 0, 0) };
        }
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        foreach (var (service, method) in StartupCalls)
        {
            Assert.Same(PilotCallResult.Unsupported, Call(service, method, []));
        }
        context.Select(pilot);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            foreach (var (service, method) in StartupCalls)
            {
                var success = Assert.IsType<PilotCallResult.Success>(Call(service, method, [], new() { ["machoVersion"] = 1L }));
                AssertStartupReply(service, method, BlueMarshal.Decode(BlueMarshal.Encode(success.Reply)), home);
            }
        }
        foreach (var (service, method) in StartupCalls)
        {
            foreach (object?[] args in new object?[][] { [null], [pilot.PilotId], [new List<object?>()], [0L, 1L] })
            {
                Assert.IsType<PilotCallResult.Failure>(Call(service, method, args));
            }
            Assert.IsType<PilotCallResult.Failure>(Call(service, method, [], new() { ["characterID"] = 90000002L }));
            Assert.Same(PilotCallResult.Unsupported, Call(service, "UnknownMapMethod", []));
        }
        Assert.Equal(pilot, context.Pilot);
        Assert.Equal(savedPilot, world.Store.FindPilot(1000001));
        Assert.Empty(context.Notifications);
        Assert.Empty(context.NotificationsBeforeFlight);

        PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
    }

    [Fact]
    public async Task NativeMapInformationPacketsIncludeInterferenceWithoutDestinationErrors()
    {
        using TestWorld world = new();
        var home = world.Services.GetRequiredService<IPilotHomeStation>().Read(world.Store.FindPilot(1000001)!.PilotId);
        Queue<object?> input = new(
            new[] { PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000001L]) }
                .Concat(StartupCalls.Select(call => PilotServicesTests.Request(call.Service, call.Method, [])))
                .Select(packet => BlueMarshal.Decode(BlueMarshal.Encode(packet)))
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
                    output.Add(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(packet))));
                    return Task.CompletedTask;
                },
                reports.Add,
                1000001,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        Assert.DoesNotContain(output, packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var responses = output.Where(packet => packet.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(StartupCalls.Length + 1, responses.Length);
        for (int index = 0; index < StartupCalls.Length; index++)
        {
            var body = BlueRead.Tuple(BlueRead.Tuple(responses[index + 1].State, 14)[4], 1);
            AssertStartupReply(StartupCalls[index].Service, StartupCalls[index].Method, BlueMarshal.Decode(BlueRead.Bytes(body[0])), home);
            Assert.Contains($"Call {StartupCalls[index].Service}.{StartupCalls[index].Method}.", reports);
        }
    }

    private static void AssertStartupReply(string service, string method, object? reply, HomeStation home)
    {
        switch (service)
        {
            case "map":
                var weather = Assert.IsType<BlueInstance>(reply);
                Assert.Equal("eve.common.script.sys.rowset.Rowset", weather.Name);
                var weatherFields = BlueRead.Fields(weather.State);
                Assert.Equal(
                    new[] { "locationID", "sceneType" },
                    Assert.IsType<List<object?>>(weatherFields["header"]).Select(BlueMarshal.Text)
                );
                Assert.Empty(Assert.IsType<List<object?>>(weatherFields["lines"]));
                break;
            case "solarsystemInterferenceMgr" when method == "GetLocalInterferenceState":
                Assert.Null(reply);
                break;
            case "structureDirectory":
                var bridgesAndAccess = BlueRead.Tuple(reply, 3);
                Assert.All(bridgesAndAccess, collection => Assert.Empty(Assert.IsType<List<object?>>(collection)));
                break;
            case "stationSvc":
                Assert.Empty(Assert.IsType<List<object?>>(reply));
                break;
            case "charMgr":
                var row = Assert.IsType<BlueInstance>(reply);
                Assert.Equal("utillib.KeyVal", row.Name);
                var fields = BlueRead.Fields(row.State);
                Assert.Equal(3, fields.Count);
                Assert.Equal((long)home.Id, fields["stationID"]);
                Assert.Equal((long)home.TypeId, fields["stationTypeID"]);
                Assert.Equal((long)home.SystemId, fields["solarSystemID"]);
                break;
            default:
                // An empty Python dictionary carries no key-type information.
                Assert.Empty(Assert.IsType<Dictionary<string, object?>>(reply));
                break;
        }
    }
}
