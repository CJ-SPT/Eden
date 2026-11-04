using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Docking;

public sealed class StructureServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedPilotsReceiveBooleanDockingAccessForKnownStationsAndDenialsForUnsupportedLocations(bool inSpace)
    {
        using TestWorld db = new();
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        using var scope = db.Services.CreateScope();
        var pilot = db.Store.FindPilot(1000001)!;
        if (inSpace)
        {
            pilot = pilot with { Position = new SpacePosition(1_000_000, 0, 0) };
        }
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new(), db.Objects),
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Same(PilotCallResult.Unsupported, Call([60003760L, 1L]));
        context.Select(pilot);
        foreach (
            var (location, service, allowed) in new (long, long, bool)[]
            {
                (60003760, 1, true),
                (60015159, 1, true),
                (0, 1, false),
                (60000000, 1, false),
                (1000000000001, 1, false),
                (long.MaxValue, 1, false),
                (60003760, 2, false),
                (60003760, long.MaxValue, false),
            }
        )
        {
            var result = Assert.IsType<PilotCallResult.Success>(Call([location, service]));
            Assert.Equal(allowed, Assert.IsType<bool>(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply))));
        }
        foreach (
            var args in new object?[][]
            {
                [],
                [60003760L],
                [60003760L, 1L, 90000001L],
                [null, 1L],
                ["60003760", 1L],
                [60003760d, 1L],
                [60003760L, true],
                [-1L, 1L],
                [60003760L, 0L],
                [60003760L, -1L],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(Call(args));
        }
        Assert.IsType<PilotCallResult.Failure>(Call([60003760L, 1L], new() { ["characterID"] = 90000002L }));
        Assert.Equal(pilot, context.Pilot);
        Assert.Empty(context.Notifications);

        PilotCallResult Call(object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(
                context,
                "structureSettings",
                "CharacterHasService",
                BlueMarshal.Decode(BlueMarshal.Encode(args)),
                _ => { },
                keywords
            );
    }

    [Fact]
    public async Task NativeLocalServicePacketsReturnBooleansInsteadOfInvalidPacketDestination()
    {
        using TestWorld db = new();
        Queue<object?> input = new(
            new[]
            {
                PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
                PilotServicesTests.Request("structureSettings", "CharacterHasService", [0L, 1L]),
                PilotServicesTests.Request("structureSettings", "CharacterHasService", [60003760L, 1L]),
                PilotServicesTests.Request("structureSettings", "CharacterHasService", [1000000000001L, 1L]),
            }.Select(packet => BlueMarshal.Decode(BlueMarshal.Encode(packet)))
        );
        List<BlueInstance> output = [];
        List<string> reports = [];
        using var scope = db.Services.CreateScope();
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
        Assert.DoesNotContain(output, packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var responses = output.Where(packet => packet.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).TakeLast(3).ToArray();
        Assert.Equal(3, responses.Length);
        Assert.Equal(
            new[] { false, true, false },
            responses.Select(packet =>
            {
                var body = BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1);
                return Assert.IsType<bool>(BlueMarshal.Decode(BlueRead.Bytes(body[0])));
            })
        );
        Assert.Equal(3, reports.Count(report => report == "Call structureSettings.CharacterHasService."));
    }
}
