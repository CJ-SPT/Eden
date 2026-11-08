using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class ShipCosmeticsServiceTests
{
    private static CosmeticCall[] Reads(long pilotId, int typeId) =>
        [
            new("GetLicencedSkins", []),
            new("GetLicencedSkinsForShipType", [pilotId, (long)typeId]),
            new("GetFirstPartySkinData", [pilotId, 1L]),
            new("GetFirstPartySkinData", [90000002L, 1L]),
        ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkinLicenseReadsRequireSelectionAndPreserveUnlicensedState(bool inSpace)
    {
        using TestWorld world = new();
        var savedPilot = world.Store.FindPilot(1000001)!;
        var otherPilot = world.Store.FindPilot(1000002)!;
        var pilot = inSpace ? savedPilot with { Position = new SpacePosition(1_000_000, 0, 0) } : savedPilot;
        using var scope = world.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        var reads = Reads(pilot.PilotId, pilot.ShipTypeId);
        foreach (var call in reads)
        {
            Assert.Same(PilotCallResult.Unsupported, Call(call));
        }
        context.Select(pilot);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            foreach (var call in reads)
            {
                var reply = Assert.IsType<PilotCallResult.Success>(Call(call, new() { ["machoVersion"] = 1L }));
                AssertReply(call.Method, BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)));
            }
        }
        foreach (var call in reads.DistinctBy(call => call.Method))
        {
            object?[][] invalidArguments = call.Method switch
            {
                "GetLicencedSkins" =>
                [
                    [pilot.PilotId],
                    [null],
                ],
                "GetLicencedSkinsForShipType" =>
                [
                    [],
                    [pilot.PilotId],
                    [otherPilot.PilotId, (long)pilot.ShipTypeId],
                    [pilot.PilotId, null],
                    [pilot.PilotId, "587"],
                    [pilot.PilotId, 0L],
                    [pilot.PilotId, (long)int.MaxValue + 1],
                ],
                _ =>
                [
                    [],
                    [pilot.PilotId],
                    [null, 1L],
                    [-1L, 1L],
                    [pilot.PilotId, 0L],
                    [pilot.PilotId, (long)int.MaxValue + 1],
                ],
            };
            foreach (var args in invalidArguments)
            {
                Assert.IsType<PilotCallResult.Failure>(Call(call with { Args = args }));
            }
            Assert.IsType<PilotCallResult.Failure>(Call(call with { Args = [.. call.Args, null] }));
            Assert.IsType<PilotCallResult.Failure>(Call(call, new() { ["characterID"] = otherPilot.PilotId }));
        }
        Assert.Same(PilotCallResult.Unsupported, Call(new("ActivateSkinLicense", [1L])));
        Assert.Same(PilotCallResult.Unsupported, Call(new("ApplySkinToShip", [pilot.ShipId, 1L])));
        Assert.Same(PilotCallResult.Unsupported, Call(new("UnknownCosmeticsMethod", [])));
        Assert.Equal(pilot, context.Pilot);
        Assert.Equal(savedPilot, world.Store.FindPilot(1000001));
        Assert.Equal(otherPilot, world.Store.FindPilot(1000002));
        Assert.Empty(context.Notifications);
        Assert.Empty(context.NotificationsBeforeFlight);

        PilotCallResult Call(CosmeticCall call, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, "shipCosmeticsMgr", call.Method, BlueMarshal.Decode(BlueMarshal.Encode(call.Args)), _ => { }, keywords);
    }

    [Fact]
    public async Task NativePersonalizationSkinPacketsReturnEmptyLicenseListsWithoutDestinationErrors()
    {
        using TestWorld world = new();
        var pilot = world.Store.FindPilot(1000001)!;
        var reads = Reads(pilot.PilotId, pilot.ShipTypeId);
        Queue<object?> input = new(
            new[] { PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [pilot.PilotId]) }
                .Concat(reads.Select(call => PilotServicesTests.Request("shipCosmeticsMgr", call.Method, call.Args)))
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
        var replies = output.Where(packet => packet.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(reads.Length + 1, replies.Length);
        for (int index = 0; index < reads.Length; index++)
        {
            var body = BlueRead.Tuple(BlueRead.Tuple(replies[index + 1].State, 14)[4], 1);
            AssertReply(reads[index].Method, BlueMarshal.Decode(BlueRead.Bytes(body[0])));
        }
    }

    private static void AssertReply(string method, object? reply)
    {
        if (method == "GetFirstPartySkinData")
        {
            Assert.Null(reply);
        }
        else
        {
            Assert.Empty(Assert.IsType<List<object?>>(reply));
        }
    }

    private sealed record CosmeticCall(string Method, object?[] Args);
}
