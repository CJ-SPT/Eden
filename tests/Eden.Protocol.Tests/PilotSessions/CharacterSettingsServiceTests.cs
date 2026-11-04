using Eden.Gameplay.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CharacterSettingsServiceTests
{
    [Fact]
    public void NativeCallsRequireSelectionAndRoundTripOpaqueValuesWithoutEditingAnotherPilot()
    {
        using TestWorld db = new();
        using var scope = db.Services.CreateScope();
        var pilot = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new(), db.Objects),
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, "charMgr", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
        Assert.IsType<PilotCallResult.UnsupportedCall>(Call("GetCharacterSettings", []));
        Assert.IsType<PilotCallResult.UnsupportedCall>(Call("SaveCharacterSetting", ["layout", "hidden"]));
        context.Select(pilot);
        Assert.Empty(BlueRead.Fields(Reply(Call("GetCharacterSettings", []))));
        Assert.Null(Reply(Call("SaveCharacterSetting", ["layout", new BlueUnicode("星間旅行 🚀")])));
        Assert.Null(Reply(Call("SaveCharacterSetting", ["opaque", new byte[] { 0, 255, 128, 10 }])));
        var values = BlueRead.Fields(Reply(Call("GetCharacterSettings", [])));
        Assert.Equal("星間旅行 🚀", values["layout"]);
        Assert.Equal(new byte[] { 0, 255, 128, 10 }, Assert.IsType<byte[]>(values["opaque"]));
        foreach (
            object?[] args in new object?[][]
            {
                [],
                ["layout"],
                [90000002L, "layout", "forged"],
                ["layout", 7L],
                ["layout", null],
                ["layout", new byte[CharacterSettingsRules.MaximumValueLength + 1]],
                ["", "bad"],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(Call("SaveCharacterSetting", args));
        }
        Assert.IsType<PilotCallResult.Failure>(Call("GetCharacterSettings", [90000002L]));
        Assert.IsType<PilotCallResult.Failure>(Call("DeleteCharacterSetting", [90000002L, "layout"]));
        Assert.IsType<PilotCallResult.Failure>(Call("DeleteCharacterSetting", ["layout"], new() { ["pilotId"] = 90000002L }));
        Assert.Equal("星間旅行 🚀", BlueRead.Fields(Reply(Call("GetCharacterSettings", [])))["layout"]);
        Assert.Empty(db.Services.GetRequiredService<IPilotCharacterSettings>().Read(90000002));
        Assert.Null(Reply(Call("DeleteCharacterSetting", ["layout"])));
        Assert.Null(Reply(Call("DeleteCharacterSetting", ["layout"])));
        Assert.Single(BlueRead.Fields(Reply(Call("GetCharacterSettings", []))));
        Assert.Empty(context.Notifications);
    }

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));
}
