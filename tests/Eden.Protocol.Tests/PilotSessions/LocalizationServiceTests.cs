using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void QaWrappingIsConnectionLocalAndValidatesTheNativeBooleanBeforeAndAfterSelection()
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var pilot = world.Store.FindPilot(1000001)!;
        PilotCallContext CreateContext(long sessionId) =>
            new(
                1000001,
                pilot.Account,
                [pilot],
                scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
                sessionId,
                SignedSettings.Empty
            );
        using var context = CreateContext(42);
        using var peer = CreateContext(43);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        PilotCallResult Call(object? arguments, Dictionary<string, object?>? keywords = null) =>
            router.Call(
                context,
                "localizationServer",
                "UpdateLocalizationQAWrap",
                BlueMarshal.Decode(BlueMarshal.Encode(arguments)),
                _ => { },
                keywords
            );
        void Set(bool enabled)
        {
            var result = Assert.IsType<PilotCallResult.Success>(Call(new object?[] { enabled }, new() { ["machoVersion"] = 1L }));
            Assert.Null(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)));
            Assert.Equal(enabled, context.LocalizationQaWrap);
            Assert.False(peer.LocalizationQaWrap);
            Assert.Empty(context.Notifications);
            Assert.Empty(context.NotificationsBeforeFlight);
        }
        Assert.False(context.LocalizationQaWrap);
        foreach (bool selected in new[] { false, true })
        {
            if (selected)
            {
                context.Select(pilot);
            }
            Set(false);
            Set(true);
            Set(true);
            foreach (
                object? invalid in new object?[]
                {
                    null,
                    new List<object?> { true },
                    Array.Empty<object?>(),
                    new object?[] { true, false },
                }
            )
            {
                Assert.IsType<PilotCallResult.Failure>(Call(invalid));
                Assert.True(context.LocalizationQaWrap);
            }
            foreach (object? invalid in new object?[] { null, 0L, 1L, "True", new List<object?>(), new Dictionary<string, object?>() })
            {
                Assert.IsType<PilotCallResult.Failure>(Call(new object?[] { invalid }));
                Assert.True(context.LocalizationQaWrap);
            }
            Assert.IsType<PilotCallResult.Failure>(Call(new object?[] { false }, new() { ["characterID"] = pilot.PilotId }));
            Assert.True(context.LocalizationQaWrap);
            Assert.Same(
                PilotCallResult.Unsupported,
                router.Call(context, "localizationServer", "Unknown", new object?[] { false }, _ => { })
            );
            Assert.Same(
                PilotCallResult.Unsupported,
                router.Call(context, "otherService", "UpdateLocalizationQAWrap", new object?[] { false }, _ => { })
            );
            Set(false);
        }
        Set(true);
        context.LogOff();
        Assert.True(context.LocalizationQaWrap);
        using var reconnect = CreateContext(44);
        Assert.False(reconnect.LocalizationQaWrap);
        Assert.Equal(pilot, world.Store.FindPilot(1000001));
    }

    [Fact]
    public async Task NativeSessionReturnsNoneForSettingsAndSelectionUpdatesWithoutInvalidDestinationErrors()
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        Queue<object?> requests = new([
            PilotServicesTests.Request("localizationServer", "UpdateLocalizationQAWrap", [false]),
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            PilotServicesTests.Request("localizationServer", "UpdateLocalizationQAWrap", [true]),
            PilotServicesTests.Request("localizationServer", "UpdateLocalizationQAWrap", [false]),
        ]);
        List<BlueInstance> packets = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                () => requests.TryDequeue(out var request) ? Task.FromResult(request) : throw new EndOfStreamException(),
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
        Assert.Equal(4, replies.Length);
        foreach (var reply in new[] { replies[0], replies[2], replies[3] })
        {
            var payload = BlueRead.Tuple(BlueRead.Tuple(reply.State, 14)[4], 1);
            Assert.Null(BlueMarshal.Decode(BlueRead.Bytes(payload[0])));
        }
    }
}
