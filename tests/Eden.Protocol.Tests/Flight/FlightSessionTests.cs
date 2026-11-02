using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;

namespace Eden.Protocol.Tests.Flight;

public sealed class FlightSessionTests
{
    private sealed class Clock : TimeProvider
    {
        public long Seconds { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    [Fact]
    public void UndockFlyStopAndDockSurviveWorldReopens()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        Clock clock = new();
        FlightSession session = new(db.Store, pilot, clock);
        Assert.Null(session.Call("ship", "Undock", [pilot.ShipId, false]));
        var spawn = Assert.IsType<SpacePosition>(session.Pilot.Position);
        Assert.Equal(pilot.SystemId, session.Pilot.LocationId);
        Assert.Null(session.Call("beyonce", "CmdGotoDirection", [0d, 2d, 0d]));
        clock.Seconds = 3;
        Assert.Null(session.Call("beyonce", "CmdStop", []));
        var stopped = Assert.IsType<SpacePosition>(session.Pilot.Position);
        Assert.True(stopped.Y > spawn.Y);
        Assert.Equal(spawn.X, stopped.X);
        clock.Seconds = 60;
        session.Checkpoint();
        var reopened = new SqliteWorld(db.Path);
        Assert.Equal(session.Pilot, reopened.FindPilot(1000001));
        Assert.Equal(stopped, session.Pilot.Position);
        var resumed = new FlightSession(reopened, reopened.FindPilot(1000001)!, clock);
        Assert.Null(resumed.Call("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Equal(pilot, new SqliteWorld(db.Path).FindPilot(1000001));
    }

    [Fact]
    public void SnapshotCarriesTheOwnedShipAndStationAtTheirSavedCoordinates()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        SpacePosition position = new(pilot.X + 123456, pilot.Y - 590, pilot.Z + 200);
        var snapshot = Ballpark.Snapshot(pilot with { Position = position }, 42);
        var fields = Assert.IsType<Dictionary<string, object?>>(snapshot.State);
        Assert.Equal(pilot.ShipId, fields["ego"]);
        var items = Assert.IsType<List<object?>>(fields["slims"]);
        Assert.Equal(2, items.Count);
        var ibis = Assert.IsType<BlueInstance>(items[1]);
        Assert.Equal(pilot.ShipName, Assert.IsType<Dictionary<string, object?>>(ibis.State)["name"]);

        using BinaryReader reader = new(new MemoryStream(Assert.IsType<byte[]>(fields["state"])));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(42, reader.ReadInt32());
        Assert.Equal(pilot.StationId, reader.ReadInt64());
        Assert.Equal(11, reader.ReadByte());
        Assert.Equal(100000f, reader.ReadSingle());
        Assert.Equal(new SpacePosition(pilot.X, pilot.Y, pilot.Z), ReadPosition(reader));
        Assert.Equal(6, reader.ReadByte());
        Assert.Equal(-1, reader.ReadSByte());
        Assert.Equal(pilot.ShipId, reader.ReadInt64());
        Assert.Equal(2, reader.ReadByte());
        Assert.Equal(47f, reader.ReadSingle());
        Assert.Equal(position, ReadPosition(reader));
        Assert.Equal(13, reader.ReadByte());
    }

    private static SpacePosition ReadPosition(BinaryReader reader) =>
        new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());

    [Fact]
    public void ApproachReachesStationAndChecksActiveShipBeforeDocking()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        Clock clock = new();
        FlightSession session = new(db.Store, pilot, clock);
        session.Call("ship", "Undock", [pilot.ShipId]);
        session.Call("beyonce", "CmdGotoDirection", [1d, 0d, 0d]);
        clock.Seconds = 60;
        Assert.Same(DockedServices.Unsupported, session.Call("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Null(session.Call("beyonce", "CmdFollowBall", [(long)pilot.StationId, 0d]));
        clock.Seconds = 180;
        session.Checkpoint();
        Assert.False(session.Moving);
        Assert.Same(DockedServices.Unsupported, session.Call("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId + 1]));
        Assert.Null(session.Call("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
    }

    [Fact]
    public void RejectsWrongShipInvalidCourseAndRemoteDock()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        Clock clock = new();
        FlightSession session = new(db.Store, pilot, clock);
        Assert.Same(DockedServices.Unsupported, session.Call("ship", "Undock", [pilot.ShipId + 1]));
        Assert.Null(session.Call("ship", "Undock", [pilot.ShipId]));
        Assert.Same(DockedServices.Unsupported, session.Call("beyonce", "CmdGotoDirection", [double.NaN, 0d, 0d]));
        Assert.Same(DockedServices.Unsupported, session.Call("beyonce", "CmdDock", [(long)pilot.StationId + 1, pilot.ShipId]));
        Assert.Null(session.Call("beyonce", "CmdGotoDirection", [1d, 0d, 0d]));
        clock.Seconds = 60;
        Assert.Same(DockedServices.Unsupported, session.Call("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Equal(pilot.SystemId, db.Store.FindPilot(1000001)!.LocationId);
    }
}
