using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

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
    public void PeersReceiveEachOthersCoursesAndLeaveIndependently()
    {
        using TestWorld db = new();
        Clock clock = new();
        SharedSpace space = new();
        using FlightSession pilot = new(db.Store, db.Store.FindPilot(1000001)!, clock, space, db.Objects);
        using FlightSession wing = new(db.Store, db.Store.FindPilot(1000002)!, clock, space, db.Objects);
        foreach (var session in new[] { pilot, wing })
        {
            session.Call("ship", "Undock", [session.Pilot.ShipId]);
            session.Snapshot();
        }
        pilot.Updates.Clear();
        pilot.RefreshPeers();
        var add = (object?[])((object?[])Assert.Single(pilot.Updates)!)[1]!;
        Assert.Equal("AddBalls", add[0]);
        var chunk = (object?[])Assert.Single((object?[])add[1]!)!;
        using BinaryReader reader = new(new MemoryStream((byte[])chunk[0]!));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(wing.Pilot.ShipId, reader.ReadInt64());
        Assert.Equal(wing.Pilot, Assert.Single(space.Peers(pilot.Pilot)).Pilot);
        Assert.Equal(pilot.Pilot, Assert.Single(space.Peers(wing.Pilot)).Pilot);
        foreach (var (moving, observer) in new[] { (pilot, wing), (wing, pilot) })
        {
            var initial = moving.Pilot.Position!;
            moving.Call("beyonce", "CmdGotoDirection", [1d, 0d, 0d]);
            clock.Seconds += 3;
            moving.Checkpoint();
            moving.RefreshPeers();
            observer.Updates.Clear();
            observer.RefreshPeers();
            var calls = observer.Updates.Cast<object?[]>().Select(tick => (object?[])tick[1]!).ToList();
            Assert.Equal(new object?[] { "SetBallPosition", "GotoDirection", "SetSpeedFraction", "SetBallVelocity" },
                calls.Select(call => call[0]));
            Assert.All(calls, call => Assert.Equal(moving.Pilot.ShipId, ((object?[])call[1]!)[0]));
            var sample = Assert.Single(space.Peers(observer.Pilot));
            Assert.True(sample.Pilot.Position!.X > initial.X);
            Assert.True(sample.Velocity.X > 0);
            observer.Updates.Clear();
            moving.Call("beyonce", "CmdStop", []);
            moving.RefreshPeers();
            observer.RefreshPeers();
            Assert.Equal("Stop", ((object?[])((object?[])Assert.Single(observer.Updates)!)[1]!)[0]);
            var braking = Assert.Single(space.Peers(observer.Pilot));
            Assert.Null(braking.Heading);
            Assert.Equal(sample.Velocity, braking.Velocity);
            clock.Seconds += 2;
            moving.Checkpoint();
            moving.RefreshPeers();
            var slower = Assert.Single(space.Peers(observer.Pilot));
            Assert.InRange(slower.Velocity.X, 1, braking.Velocity.X - 1);
            Assert.True(slower.Pilot.Position!.X > braking.Pilot.Position!.X);
        }
        wing.Call("beyonce", "CmdDock", [(long)wing.Pilot.StationId, wing.Pilot.ShipId]);
        Assert.Empty(space.Peers(pilot.Pilot));
        pilot.Updates.Clear();
        pilot.RefreshPeers();
        var remove = (object?[])((object?[])Assert.Single(pilot.Updates)!)[1]!;
        Assert.Equal("RemoveBall", remove[0]);
        Assert.Equal(new object?[] { wing.Pilot.ShipId, false }, (object?[])remove[1]!);
        pilot.Dispose();
        Assert.Empty(space.Peers(wing.Pilot));
        Assert.Equal(pilot.Pilot, db.Store.FindPilot(1000001));
        Assert.Null(db.Store.FindPilot(1000002)!.Position);
    }

    [Fact]
    public void WarpSeparatesAndReunitesTwoShipsAndPersistsTheLandingRange()
    {
        using TestWorld db = new();
        Clock clock = new();
        SharedSpace space = new();
        using FlightSession pilot = new(db.Store, db.Store.FindPilot(1000001)!, clock, space, db.Objects);
        using FlightSession wing = new(db.Store, db.Store.FindPilot(1000002)!, clock, space, db.Objects);
        var gate = db.Objects.Single(body => body.DestinationGateId.HasValue);
        object?[] toGate = ["item"u8.ToArray(), (long)gate.Id];
        foreach (var session in new[] { pilot, wing })
        {
            session.Call("ship", "Undock", [session.Pilot.ShipId]);
            session.Snapshot();
        }
        Assert.Single(space.Peers(wing.Pilot));
        var departure = pilot.Pilot.Position;
        pilot.Updates.Clear();
        Assert.Null(pilot.Call("beyonce", "CmdWarpToStuff", toGate, new() { ["minRange"] = 10_000L }));
        Assert.Equal("GotoDirection", ((object?[])((object?[])pilot.Updates[0]!)[1]!)[0]);
        clock.Seconds = 4;
        pilot.Checkpoint();
        Assert.NotEqual(departure, pilot.Pilot.Position);
        pilot.Updates.Clear();
        clock.Seconds = 8;
        pilot.Checkpoint();
        var launch = pilot.Updates.Cast<object?[]>().Select(tick => (object?[])tick[1]!).ToArray();
        Assert.Equal(new[] { "SetSpeedFraction", "SetBallVelocity", "WarpTo", "OnSpecialFX" }, launch.Select(call => call[0]));
        var warp = launch[2];
        Assert.Equal("WarpTo", warp[0]);
        Assert.Equal(3000, ((object?[])warp[1]!)[5]);
        Assert.Same(PilotCallRouter.Unsupported, pilot.Call("beyonce", "CmdStop", []));
        clock.Seconds = 22;
        pilot.Checkpoint();
        pilot.RefreshPeers();
        Assert.Empty(space.Peers(wing.Pilot));
        Assert.True(pilot.Moving);
        Assert.Equal(pilot.Pilot.Position, db.Store.FindPilot(1000001)!.Position);
        clock.Seconds = 120;
        pilot.Checkpoint();
        pilot.RefreshPeers();
        Assert.False(pilot.Moving);
        Assert.Equal(10_500, pilot.Pilot.Position!.DistanceTo(gate.Position) - gate.Radius - pilot.Pilot.Flight.ShipRadius, 2);
        Assert.Same(PilotCallRouter.Unsupported, pilot.Call("beyonce", "CmdWarpToStuff", toGate));
        Assert.Null(wing.Call("beyonce", "CmdWarpToStuff", toGate));
        clock.Seconds += 8;
        wing.Checkpoint();
        clock.Seconds += 120;
        wing.Checkpoint();
        wing.RefreshPeers();
        Assert.Equal(pilot.Pilot, Assert.Single(space.Peers(wing.Pilot)).Pilot);
        Assert.Equal(wing.Pilot, new SqliteWorld(db.Path).FindPilot(1000002));
        Assert.Null(pilot.Call("beyonce", "CmdWarpToStuff", ["item", (long)pilot.Pilot.StationId]));
        clock.Seconds += 8;
        pilot.Checkpoint();
        clock.Seconds += 120;
        pilot.Checkpoint();
        Assert.Null(pilot.Call("beyonce", "CmdDock", [(long)pilot.Pilot.StationId, pilot.Pilot.ShipId]));
    }

    [Fact]
    public void WarpRequiresAValidTargetRangeAndInSpaceShip()
    {
        using TestWorld db = new();
        using FlightSession session = new(db.Store, db.Store.FindPilot(1000001)!, new Clock(), new(), db.Objects);
        object?[] target = ["item", (long)db.Objects.Single(body => body.DestinationGateId.HasValue).Id];
        Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdWarpToStuff", target));
        session.Call("ship", "Undock", [session.Pilot.ShipId]);
        session.Snapshot();
        var origin = session.Pilot.Position;
        Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdWarpToStuff", ["item", 1L]));
        Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdWarpToStuff", ["bookmark", target[1]]));
        foreach (object range in new object[] { -1L, 100_001L, double.NaN, "bad" })
        {
            Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdWarpToStuff", target, new() { ["minRange"] = range }));
        }
        Assert.Equal(origin, session.Pilot.Position);
        Assert.False(session.Moving);
    }

    [Fact]
    public void StaticObjectsAreScopedToTheSystemAndIncludeClientOnlyBodies()
    {
        using TestWorld db = new();
        var objects = db.Services.GetRequiredService<ISystemObjects>();
        Assert.Equal(new[] { 40009087, 50001249, 60003760 }, objects.Load(30000142).Select(body => body.Id));
        var other = objects.Load(30000144);
        Assert.Equal(new[] { 40455847, 50016295 }, other.Select(body => body.Id));
        Assert.Equal(new SpacePosition(1, 2, 3), other[0].Position);
        Assert.Equal(10000f, other[0].Radius);
        Assert.Null(other[1].DestinationGateId);
        Assert.Empty(objects.Load(1));
    }

    [Fact]
    public void LongInwardCourseStopsAtTheNearStationSurface()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        Clock clock = new();
        using FlightSession session = new(db.Store, pilot, clock, new(), db.Objects);
        session.Call("ship", "Undock", [pilot.ShipId]);
        session.Call("beyonce", "CmdGotoDirection", [-1d, 0d, 0d]);
        clock.Seconds = 1000;
        session.Checkpoint();
        Assert.False(session.Moving);
        Assert.Equal(pilot.X + pilot.Flight.StationRadius + pilot.Flight.ShipRadius, session.Pilot.Position!.X);
        Assert.Equal(session.Pilot, db.Store.FindPilot(1000001));
    }

    [Fact]
    public void UndockFlyStopAndDockSurviveWorldReopens()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        Clock clock = new();
        FlightSession session = new(db.Store, pilot, clock, new(), db.Objects);
        Assert.Null(session.Call("ship", "Undock", [pilot.ShipId, false]));
        var spawn = Assert.IsType<SpacePosition>(session.Pilot.Position);
        Assert.Equal(pilot.SystemId, session.Pilot.LocationId);
        Assert.Null(session.Call("beyonce", "CmdGotoDirection", [0d, 2d, 0d]));
        clock.Seconds = 3;
        session.Updates.Clear();
        Assert.Null(session.Call("beyonce", "CmdStop", []));
        Assert.Equal("Stop", ((object?[])((object?[])Assert.Single(session.Updates)!)[1]!)[0]);
        session.Updates.Clear();
        var stopped = Assert.IsType<SpacePosition>(session.Pilot.Position);
        Assert.True(stopped.Y > spawn.Y);
        Assert.Equal(spawn.X, stopped.X);
        clock.Seconds = 60;
        session.Checkpoint();
        var reopened = new SqliteWorld(db.Path);
        Assert.Equal(session.Pilot, reopened.FindPilot(1000001));
        Assert.Empty(session.Updates);
        Assert.True(session.Pilot.Position!.Y > stopped.Y);
        Assert.False(session.Moving);
        var resumed = new FlightSession(reopened, reopened.FindPilot(1000001)!, clock, new(), db.Objects);
        Assert.Null(resumed.Call("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Equal(pilot, new SqliteWorld(db.Path).FindPilot(1000001));
    }

    [Fact]
    public void SnapshotCarriesTheOwnedShipAndStationAtTheirSavedCoordinates()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        SpacePosition position = new(pilot.X + 123456, pilot.Y - 590, pilot.Z + 200);
        var snapshot = Ballpark.Snapshot(pilot with { Position = position }, 42, db.Objects);
        var fields = Assert.IsType<Dictionary<string, object?>>(snapshot.State);
        Assert.Equal(pilot.ShipId, fields["ego"]);
        var items = Assert.IsType<List<object?>>(fields["slims"]);
        Assert.Equal(db.Objects.Count + 1, items.Count);
        var ibis = Assert.IsType<BlueInstance>(items[^1]);
        Assert.Equal(pilot.ShipName, Assert.IsType<Dictionary<string, object?>>(ibis.State)["name"]);

        using BinaryReader reader = new(new MemoryStream(Assert.IsType<byte[]>(fields["state"])));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(42, reader.ReadInt32());
        foreach (var body in db.Objects)
        {
            Assert.Equal(body.Id, reader.ReadInt64());
            Assert.Equal(11, reader.ReadByte());
            Assert.Equal(body.Radius, reader.ReadSingle());
            Assert.Equal(body.Position, ReadPosition(reader));
            Assert.Equal(6, reader.ReadByte());
            Assert.Equal(-1, reader.ReadSByte());
        }
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
        FlightSession session = new(db.Store, pilot, clock, new(), db.Objects);
        session.Call("ship", "Undock", [pilot.ShipId]);
        session.Call("beyonce", "CmdGotoDirection", [1d, 0d, 0d]);
        clock.Seconds = 60;
        Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Null(session.Call("beyonce", "CmdFollowBall", [(long)pilot.StationId, 0d]));
        clock.Seconds = 180;
        session.Checkpoint();
        Assert.False(session.Moving);
        Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId + 1]));
        Assert.Null(session.Call("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
    }

    [Fact]
    public void RejectsWrongShipInvalidCourseAndRemoteDock()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        Clock clock = new();
        FlightSession session = new(db.Store, pilot, clock, new(), db.Objects);
        Assert.Same(PilotCallRouter.Unsupported, session.Call("ship", "Undock", [pilot.ShipId + 1]));
        Assert.Null(session.Call("ship", "Undock", [pilot.ShipId]));
        Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdGotoDirection", [double.NaN, 0d, 0d]));
        Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdDock", [(long)pilot.StationId + 1, pilot.ShipId]));
        Assert.Null(session.Call("beyonce", "CmdGotoDirection", [1d, 0d, 0d]));
        clock.Seconds = 60;
        Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Equal(pilot.SystemId, db.Store.FindPilot(1000001)!.LocationId);
    }
}
