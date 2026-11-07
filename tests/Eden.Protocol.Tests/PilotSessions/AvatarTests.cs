using Eden.Protocol.Characters;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class AvatarTests
{
    private static BlueInstance Avatar(object? field) => new("utillib.KeyVal", new Dictionary<string, object?> { ["appearance"] = field });

    [Fact]
    public void NormalAppearanceAndSharedFieldsKeepTheirWireForm()
    {
        object?[] fields = [1L, 0.5d, null];
        var avatar = Avatar(
            new object?[] { new PaperDollRow("eve.common.script.paperDoll.paperDollDefinitions.AppearanceRow", fields), fields, fields }
        );
        Assert.Equal(BlueMarshal.Encode(avatar), AvatarWire.Encode(avatar));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void ExpandedSharedAppearanceOrPortraitCannotCreateAPilot(int field)
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var pilot = world.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new FlightSession(world.Store, selected, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        object value = 1L;
        for (int i = 0; i < 24; i++)
        {
            value = new object?[] { value, value };
        }
        object?[] args = ["Oversized Pilot", 1L, 1L, 0L, 64L, Avatar(null), Avatar(null), 18L, null, null];
        args[field] = Avatar(value);
        Assert.IsType<PilotCallResult.Failure>(
            scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(context, "charUnboundMgr", "CreateCharacterWithDoll", args, _ => { })
        );
        using WorldDbContext saved = new(world.Path);
        Assert.Equal(2, saved.Pilots.Count());
        Assert.Equal(2, saved.Ships.Count());
    }

    [Fact]
    public void OversizedScalarIsRejectedBeforeEncoding()
    {
        Assert.Throws<InvalidDataException>(() => AvatarWire.Encode(Avatar(new byte[1024 * 1024])));
    }
}
