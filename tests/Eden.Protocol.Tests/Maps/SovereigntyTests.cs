using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Maps;

public sealed class SovereigntyTests
{
    private const long HomeSystemId = 30000142;
    private const long NullSecuritySystemId = 30003995; // C-7SBM in the build 3662168 catalog.

    // PanelSov reads indices twice and obtains hub information while computing the
    // strategic index. Include those repeated calls in their native loading order.
    private static SovereigntyCall[] PanelCalls(long systemId) =>
        [
            new("sovMgr", "GetSovStructuresInfoForLocalSolarSystem", []),
            new("sovMgr", "GetSovStructuresInfoForSolarSystem", [systemId]),
            new("devIndexManager", "GetDevelopmentIndicesForSystem", [systemId]),
            new("sovMgr", "GetInfrastructureHubInfo", [systemId]),
            new("sovMgr", "GetSystemSovereigntyInfo", [systemId]),
            new("devIndexManager", "GetDevelopmentIndicesForSystem", [systemId]),
            new("sovMgr", "GetInfrastructureHubInfo", [systemId]),
        ];

    [Theory]
    [InlineData(false, HomeSystemId)]
    [InlineData(true, HomeSystemId)]
    [InlineData(false, NullSecuritySystemId)]
    [InlineData(true, NullSecuritySystemId)]
    public void SovereigntyReadsPreserveNativeShapesSelectionAndSavedState(bool inSpace, long systemId)
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var savedPilot = world.Store.FindPilot(1000001)!;
        var pilot = inSpace ? savedPilot with { Position = new SpacePosition(1_000_000, 0, 0) } : savedPilot;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        foreach (var call in PanelCalls(systemId))
        {
            Assert.Same(PilotCallResult.Unsupported, Call(call));
        }
        context.Select(pilot);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            foreach (var call in PanelCalls(systemId))
            {
                var success = Assert.IsType<PilotCallResult.Success>(Call(call, new() { ["machoVersion"] = 1L }));
                AssertReply(call.Method, BlueMarshal.Decode(BlueMarshal.Encode(success.Reply)));
            }
        }
        foreach (var call in PanelCalls(systemId).DistinctBy(call => (call.Service, call.Method)))
        {
            object?[][] invalidArguments =
                call.Method == "GetSovStructuresInfoForLocalSolarSystem"
                    ?
                    [
                        [systemId],
                        [null],
                        [systemId, systemId],
                    ]
                    :
                    [
                        [],
                        [null],
                        [true],
                        ["30000142"],
                        [1.0],
                        [0L],
                        [-1L],
                        [(long)int.MaxValue + 1],
                        [systemId, systemId],
                    ];
            foreach (var args in invalidArguments)
            {
                Assert.IsType<PilotCallResult.Failure>(Call(call with { Args = args }));
            }
            Assert.IsType<PilotCallResult.Failure>(Call(call, new() { ["solarSystemID"] = systemId }));
            Assert.Same(PilotCallResult.Unsupported, Call(call with { Method = "UnknownSovereigntyMethod" }));
        }
        Assert.Same(PilotCallResult.Unsupported, Call(new("unrelatedService", "GetDevelopmentIndicesForSystem", [systemId])));
        Assert.Equal(pilot, context.Pilot);
        Assert.Equal(savedPilot, world.Store.FindPilot(1000001));
        Assert.Empty(context.Notifications);
        Assert.Empty(context.NotificationsBeforeFlight);

        PilotCallResult Call(SovereigntyCall call, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, call.Service, call.Method, BlueMarshal.Decode(BlueMarshal.Encode(call.Args)), _ => { }, keywords);
    }

    [Fact]
    public async Task NativeSovereigntyPanelPacketsReturnUnclaimedSystemInformationWithoutDestinationErrors()
    {
        using TestWorld world = new();
        var calls = PanelCalls(NullSecuritySystemId);
        Queue<object?> input = new(
            new[] { PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000001L]) }
                .Concat(calls.Select(call => PilotServicesTests.Request(call.Service, call.Method, call.Args)))
                .Select(packet => BlueMarshal.Decode(BlueMarshal.Encode(packet)))
        );
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
        var responses = output.Where(packet => packet.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(calls.Length + 1, responses.Length);
        for (int index = 0; index < calls.Length; index++)
        {
            var body = BlueRead.Tuple(BlueRead.Tuple(responses[index + 1].State, 14)[4], 1);
            AssertReply(calls[index].Method, BlueMarshal.Decode(BlueRead.Bytes(body[0])));
        }
    }

    private static void AssertReply(string method, object? reply)
    {
        switch (method)
        {
            case "GetSovStructuresInfoForSolarSystem":
            case "GetSovStructuresInfoForLocalSolarSystem":
                Assert.Empty(Assert.IsType<List<object?>>(reply));
                break;
            case "GetDevelopmentIndicesForSystem":
                var indices = Assert.IsType<Dictionary<long, object?>>(reply);
                // Independent catalog IDs ensure the wire keys remain the native dogma attributes.
                Assert.Equal(new long[] { 1583, 1584 }, indices.Keys.Order().ToArray());
                foreach (var value in indices.Values)
                {
                    var row = Assert.IsType<BlueInstance>(value);
                    Assert.Equal("utillib.KeyVal", row.Name);
                    var fields = BlueRead.Fields(row.State);
                    Assert.Equal(2, fields.Count);
                    Assert.Equal(0L, fields["points"]);
                    Assert.Equal(false, fields["increasing"]);
                }
                break;
            default:
                Assert.Null(reply);
                break;
        }
    }

    private sealed record SovereigntyCall(string Service, string Method, object?[] Args);
}
