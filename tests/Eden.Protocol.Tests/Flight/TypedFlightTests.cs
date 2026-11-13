using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Combat;

namespace Eden.Protocol.Tests.Flight;

public sealed class TypedFlightTests
{
    // Build-3662168 inventorycommon.const.groupConcordDrone.
    private const int ConcordNpcGroup = 301;

    [Theory]
    [InlineData(NpcRole.Pirate, 11, 11)]
    [InlineData(NpcRole.Convoy, -11, -11)]
    [InlineData(NpcRole.Navy, -11, -11)]
    [InlineData(NpcRole.Customs, -11, -11)]
    [InlineData(NpcRole.Concord, -11, -11)]
    public void NpcSlimsAlwaysSupplyStandingThresholdsForNativeOverviewRefresh(NpcRole role, double hostile, double friendly)
    {
        using NpcCombatTests.Fixture fixture = new();
        var npc = fixture.Npc with { Role = role, StandingThresholds = null };
        var slim = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new NpcSlimItem(npc))));
        var fields = BlueRead.Fields(slim.State);
        Assert.Equal(hostile, Assert.IsType<double>(fields["hostile_response_threshold"]));
        Assert.Equal(friendly, Assert.IsType<double>(fields["friendly_response_threshold"]));
    }

    [Theory]
    [InlineData(ConcordRules.CaptainType, false)]
    [InlineData(ConcordRules.CaptainType, true)]
    [InlineData(ConcordRules.CommanderType, false)]
    [InlineData(ConcordRules.CommanderType, true)]
    public void ConcordSnapshotAndAdditionsStayFriendlyWithHostileCatalogThresholdsAndAnActiveWeapon(int type, bool attacking)
    {
        using NpcCombatTests.Fixture fixture = new();
        var npc = fixture.Npc with
        {
            TypeId = type,
            GroupId = ConcordNpcGroup,
            CorporationId = ConcordRules.Corporation,
            Role = NpcRole.Concord,
            StandingThresholds = new(11, 11),
            Weapon = attacking ? new(NpcCombatTests.Fixture.ShipId, 0, fixture.Clock.Now, 5000) : null,
        };
        ShipContact own = new(PilotContact.From(fixture.Pilot), SpacePosition.Zero, null, 0, null);
        var snapshot = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(Ballpark.Snapshot(own, 1, [], [], [npc]))));
        var slims = Assert.IsType<List<object?>>(BlueRead.Fields(snapshot.State)["slims"]);
        var addition = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(Ballpark.AddNpcs([npc], 2))), 3);
        var added = Assert.Single(Assert.IsType<List<object?>>(addition[1]));
        foreach (var item in new[] { slims[1], added })
        {
            var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(item).State);
            Assert.Equal((long)ConcordNpcGroup, fields["groupID"]);
            Assert.Equal((long)ConcordRules.Corporation, fields["ownerID"]);
            Assert.Equal(-11d, Assert.IsType<double>(fields["hostile_response_threshold"]));
            Assert.Equal(-11d, Assert.IsType<double>(fields["friendly_response_threshold"]));
        }
        Assert.Equal(attacking, npc.Weapon is not null);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void StaticObjectVisibilityDoesNotChangeCollisionOrRigidPacketLayout(bool global, bool asteroid, bool solid)
    {
        StaticSpaceObject body = new(42, 1230, 226, asteroid ? 25 : 2, 1, new(10, 20, 30), 100) { IsGlobal = global, Solid = solid };
        using BinaryReader reader = new(new MemoryStream(Ballpark.AddObjects([body], 7).State));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(7, reader.ReadInt32());
        Assert.Equal(body.Id, reader.ReadInt64());
        Assert.Equal((byte)NavigationMode.Rigid, reader.ReadByte());
        Assert.Equal(body.Radius, reader.ReadSingle());
        Assert.Equal(body.Position.X, reader.ReadDouble());
        Assert.Equal(body.Position.Y, reader.ReadDouble());
        Assert.Equal(body.Position.Z, reader.ReadDouble());
        var flags = (DestinyBallFlags)reader.ReadByte();
        Assert.Equal(global && !asteroid, flags.HasFlag(DestinyBallFlags.Global));
        Assert.Equal(solid, flags.HasFlag(DestinyBallFlags.Massive));
        Assert.False(flags.HasFlag(DestinyBallFlags.Free));
        Assert.Equal(-1, reader.ReadSByte());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
    }

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
                    ["dbuffState"] = new List<object?>(),
                    ["industryLevel"] = 0,
                    ["researchLevel"] = 0,
                }
            ),
            snapshot
        );
    }

    private static void Equal(object original, IBlueWritable model) =>
        Assert.Equal(BlueMarshal.Encode(original), BlueMarshal.Encode(model));

    [Fact]
    public void DeparturesUseNativeBulkCleanupWhileDestructionKeepsTheExplosionPath()
    {
        Equal(new object?[] { "RemoveBalls", new object?[] { new List<object?> { 10L } } }, new DestinyCommand.RemoveBall(10, false));
        Equal(new object?[] { "RemoveBall", new object?[] { 10L, true } }, new DestinyCommand.RemoveBall(10, true));
    }
}
