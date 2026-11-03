using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Flight;

public sealed class TypedFlightTests
{
    [Fact]
    public void DamageStateKeepsNestedShieldTupleAndFloatingTimeConstant()
    {
        ShipDamageState state = new(0.75, 172000, 0.5, 1);
        Assert.Equal(0.5, state.ArmorFraction);
        Equal(new object?[] { new object?[] { 0.75, 172000d }, 0.5, 1d }, state);
    }

    [Fact]
    public void StampedCommandsKeepNamesArgumentOrderAndIntegerWarpFactor()
    {
        StampedDestinyUpdate update = new(42, new DestinyCommand.Follow(NavigationMode.Orbit, 10, 20, 1000));
        Assert.IsType<DestinyCommand.Follow>(update.Command);
        Equal(new object?[] { 42, new object?[] { "Orbit", new object?[] { 10L, 20L, 1000d } } }, update);
        Equal(new object?[] { "WarpTo", new object?[] { 10L, 1d, 2d, 3d, 0d, 3000 } }, new DestinyCommand.WarpTo(10, 1, 2, 3, 0, 3000));
    }

    [Fact]
    public void JumpAndModuleEffectsKeepDifferentOptionalWireForms()
    {
        Equal(
            new object?[]
            {
                "OnSpecialFX",
                new object?[]
                {
                    10L,
                    null,
                    null,
                    20L,
                    null,
                    "effects.JumpOut",
                    false,
                    true,
                    false,
                    10000,
                    0,
                    null,
                    0,
                    new object?[] { 30000142 },
                },
            },
            new DestinyCommand.JumpOut(10, 20, 30000142, 10000)
        );
        Equal(
            new object?[]
            {
                "OnSpecialFX",
                new object?[] { 10L, 11L, 561, 20L, 222, "effects.ProjectileFired", true, true, true, 5000d, 0, 123L, 0, null },
            },
            new DestinyCommand.TimedSpecialEffect(10, 11, 561, 20, 222, "effects.ProjectileFired", true, true, true, 5000, 123)
        );
        var effect = new ShipEffectChanged(new(11, 12, 10, 20, DogmaEffect.ProjectileFired, 123, 5000, 0), 200, false, false, 200);
        Assert.Equal(200L, effect.Arguments[^1]);
    }

    [Fact]
    public void BallparkSnapshotExposesNamedDataAndKeepsExistingWireShape()
    {
        BallparkSnapshot snapshot = new(
            [0, 42, 0, 0, 0],
            10,
            [],
            new SolarSystemSlim(30000142),
            new Dictionary<long, ShipDamageState> { [10] = new(1, 172000, 1, 1) }
        );
        Assert.Equal(10, snapshot.Ego);
        Assert.Empty(snapshot.Slims);
        Equal(
            new BlueInstance(
                "utillib.KeyVal",
                new Dictionary<string, object?>
                {
                    ["state"] = new byte[] { 0, 42, 0, 0, 0 },
                    ["ego"] = 10L,
                    ["slims"] = new List<object?>(),
                    ["solItem"] = new BlueInstance(
                        "eve.common.script.util.slimItem.SlimItem",
                        new Dictionary<string, object?>
                        {
                            ["itemID"] = 30000142,
                            ["typeID"] = 5,
                            ["ownerID"] = 1L,
                            ["groupID"] = 5,
                            ["categoryID"] = 2,
                        }
                    ),
                    ["droneState"] = new List<object?>(),
                    ["damageState"] = new Dictionary<long, object?> { [10] = new object?[] { new object?[] { 1d, 172000d }, 1d, 1d } },
                    ["effectStates"] = new List<object?>(),
                    ["dbuffState"] = new Dictionary<long, object?>(),
                    ["industryLevel"] = 0,
                    ["researchLevel"] = 0,
                }
            ),
            snapshot
        );
    }

    private static void Equal(object original, IBlueWritable model) =>
        Assert.Equal(BlueMarshal.Encode(original), BlueMarshal.Encode(model));
}
