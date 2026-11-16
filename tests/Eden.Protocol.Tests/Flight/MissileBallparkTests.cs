using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Flight;

public sealed class MissileBallparkTests
{
    private sealed class Missiles : IPilotMissiles
    {
        internal IReadOnlyList<MissileContact> Current { get; set; } = [];

        public MissileLaunch Launch(
            long pilotId,
            long moduleId,
            long? targetId,
            IReadOnlySet<long> lockedTargets,
            MissileFiringState motion,
            long now
        ) => throw new NotSupportedException();

        public void Advance(int systemId, long now) { }

        public IReadOnlyList<MissileContact> List(int systemId) => Current;

        public bool Impacted(long projectileId) => false;

        public IReadOnlyList<MissileImpact> TakeImpacts(long pilotId) => [];

        public void Cancel(long pilotId, long shipId) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissileIndicatorsTrackEachProjectileAcrossSnapshotsOrphaningAndRemoval(bool snapshot)
    {
        using TestWorld db = new(liveWorld: true);
        var pilot = db.Store.FindPilot(1000001)! with { Position = SpacePosition.Zero };
        var first = Contact() with { TargetId = pilot.ShipId, SourceShipId = Contact().TargetId };
        var second = first with { Id = first.Id + 1 };
        Missiles missiles = new() { Current = [first, second] };
        List<DestinyCommand> updates = [];
        FlightScene scene = new(
            new(),
            new TestClock(),
            [],
            new(updates.Add),
            updates.Add,
            u => updates.Add(u.Command),
            null,
            null,
            null,
            missiles,
            null
        );
        FlightSceneContext context = new(pilot, true, true, 1);
        if (snapshot)
        {
            scene.Snapshot(context, [], new(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null));
        }
        else
        {
            scene.RefreshMissiles(context);
        }
        Assert.Equal(
            new[]
            {
                new DestinyCommand.AttackIndicator(first.SourceShipId, first.Id, pilot.ShipId, true),
                new DestinyCommand.AttackIndicator(second.SourceShipId, second.Id, pilot.ShipId, true),
            },
            updates.OfType<DestinyCommand.AttackIndicator>()
        );
        updates.Clear();
        scene.RefreshMissiles(context);
        Assert.Empty(updates);
        missiles.Current = [second];
        scene.RefreshMissiles(context);
        Assert.Equal(new DestinyCommand.AttackIndicator(first.SourceShipId, first.Id, pilot.ShipId, false), updates[0]);
        Assert.Equal(new DestinyCommand.RemoveBall(first.Id, false), updates[1]);
        updates.Clear();
        missiles.Current = [second with { Flight = second.Flight with { Orphaned = true } }];
        scene.RefreshMissiles(context);
        Assert.Equal(new DestinyCommand.AttackIndicator(second.SourceShipId, second.Id, pilot.ShipId, false), updates[0]);
        updates.Clear();
        scene.RefreshMissiles(context);
        Assert.Empty(updates);
        scene.ForgetMissiles();
        Assert.Empty(updates);
        scene.RefreshMissiles(context);
        Assert.Empty(updates.OfType<DestinyCommand.AttackIndicator>());
        missiles.Current = [first, second];
        scene.RefreshMissiles(context);
        updates.Clear();
        scene.ForgetMissiles();
        Assert.Equal(new[] { first.Id, second.Id }, updates.Cast<DestinyCommand.AttackIndicator>().Select(e => e.ModuleId));
        Assert.All(updates.Cast<DestinyCommand.AttackIndicator>(), e => Assert.False(e.Active));
        Assert.Empty(scene.VisibleMissiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttackIndicatorUsesNativeOffensiveEventsWithoutInventingAWeaponGraphic(bool active)
    {
        var command = new DestinyCommand.AttackIndicator(10, 11, 12, active);
        var wire = Assert.IsType<object?[]>(WireAssertions.Materialize(command));
        Assert.Equal("OnSpecialFX", BlueMarshal.Text(wire[0]));
        Assert.Equal(new object?[] { 10L, 11L, null, 12L, null, string.Empty, true, active, active }, Assert.IsType<object?[]>(wire[1]));
    }

    private static MissileContact Contact() =>
        new(
            6000000000001,
            212,
            384,
            30000144,
            90000001,
            1000000000001,
            [1000000100001, 1000000100002],
            4000000000001,
            100,
            1000127,
            42,
            new(new(700, 300, 3750, .000144498f, 5000), new(1, 2, 3), new(4, 5, 6), new(1000, 2000, 3000), 2)
        );

    [Fact]
    public void MissileStreamUsesCarbonModeFiveAndExactFollowerOwnerStampAndGuidanceLayout()
    {
        var missile = Contact();
        var addition = Ballpark.AddMissiles([missile], 44);
        Assert.Equal(153, addition.State.Length);
        Assert.Empty(addition.Damage);
        using BinaryReader reader = new(new MemoryStream(addition.State));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(44, reader.ReadInt32());
        Assert.Equal(missile.Id, reader.ReadInt64());
        Assert.Equal(5, reader.ReadByte());
        Assert.Equal(300f, reader.ReadSingle());
        Assert.Equal(1d, reader.ReadDouble());
        Assert.Equal(2d, reader.ReadDouble());
        Assert.Equal(3d, reader.ReadDouble());
        Assert.Equal((byte)DestinyBallFlags.Free, reader.ReadByte());
        Assert.Equal(700d, reader.ReadDouble());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(-1, reader.ReadInt64());
        Assert.Equal(missile.CorporationId, reader.ReadInt32());
        Assert.Equal(-1, reader.ReadInt32());
        Assert.Equal(3750f, reader.ReadSingle());
        Assert.Equal(4d, reader.ReadDouble());
        Assert.Equal(5d, reader.ReadDouble());
        Assert.Equal(6d, reader.ReadDouble());
        Assert.Equal(.000144498f, reader.ReadSingle());
        Assert.Equal(1f, reader.ReadSingle());
        Assert.Equal(-1, reader.ReadSByte());
        Assert.Equal(missile.TargetId, reader.ReadInt64());
        Assert.Equal(-400f, reader.ReadSingle());
        Assert.Equal(missile.SourceShipId, reader.ReadInt64());
        Assert.Equal(42, reader.ReadInt32());
        Assert.Equal(1000d, reader.ReadDouble());
        Assert.Equal(2000d, reader.ReadDouble());
        Assert.Equal(3000d, reader.ReadDouble());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        Assert.NotEmpty(BlueMarshal.Encode(addition));
    }

    [Fact]
    public void MissileSlimNamesTheLaunchingShipAndModuleListWithChargeIdentity()
    {
        var missile = Contact();
        var slim = Assert.IsType<BlueInstance>(WireAssertions.Materialize(Assert.Single(Ballpark.AddMissiles([missile], 44).Slims)));
        var fields = Assert.IsType<Dictionary<string, object?>>(slim.State);
        Assert.Equal("eve.common.script.util.slimItem.SlimItem", slim.Name);
        Assert.Equal(missile.PilotId, fields["ownerID"]);
        Assert.Equal((int)InventoryCategory.Charge, fields["categoryID"]);
        Assert.Equal(212, fields["typeID"]);
        Assert.Equal(384, fields["groupID"]);
        Assert.Equal(missile.SourceShipId, fields["sourceShipID"]);
        Assert.Equal(new object?[] { 1000000100001L, 1000000100002L }, Assert.IsType<List<object?>>(fields["launchModules"]));
    }

    [Fact]
    public void OrphanedMissileUsesGotoWithoutStaleFollowerPayload()
    {
        var missile = Contact();
        missile = missile with { Flight = missile.Flight with { Orphaned = true } };
        var addition = Ballpark.AddMissiles([missile], 44);
        Assert.Equal(129, addition.State.Length);
        Assert.Equal((byte)NavigationMode.Goto, addition.State[13]);
        using BinaryReader reader = new(new MemoryStream(addition.State));
        reader.BaseStream.Position = 105;
        Assert.Equal(1000d, reader.ReadDouble());
        Assert.Equal(2000d, reader.ReadDouble());
        Assert.Equal(3000d, reader.ReadDouble());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
    }

    [Fact]
    public void NonfiniteMissileGeometryCannotPoisonTheNativeStateStream()
    {
        var missile = Contact();
        var malformed = missile with { Flight = missile.Flight with { Velocity = new(double.NaN, 0, 0) } };
        Assert.Throws<InvalidDataException>(() => Ballpark.AddMissiles([malformed], 44));
        Assert.Throws<InvalidDataException>(() => Ballpark.AddMissiles([missile with { TargetRadius = float.NaN }], 44));
    }

    [Theory]
    [InlineData(MissileFlightStatus.Impact)]
    [InlineData(MissileFlightStatus.Expired)]
    public void CompletedMissilesCannotBeAddedAsLiveBalls(MissileFlightStatus status)
    {
        var missile = Contact();
        missile = missile with { Flight = missile.Flight with { Status = status } };
        Assert.Throws<InvalidDataException>(() => Ballpark.AddMissiles([missile], 44));
    }

    [Fact]
    public void SnapshotAndIncrementalAdditionUseTheSameMissileStateAndSlim()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero, 30000144);
        pilot = db.Store.FindPilot(1000001)!;
        ShipContact own = new(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null);
        var snapshot = Ballpark.Snapshot(own, 44, [], [], [], missiles: [Contact()]);
        var missileBytes = Ballpark.AddMissiles([Contact()], 44).State[5..];
        Assert.Equal(missileBytes, snapshot.State[^missileBytes.Length..]);
        Assert.IsType<MissileSlimItem>(snapshot.Slims[^1]);
        Assert.DoesNotContain(Contact().Id, snapshot.Damage.Keys);
    }
}
