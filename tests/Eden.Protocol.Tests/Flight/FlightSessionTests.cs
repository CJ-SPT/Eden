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
            session.CallWire("ship", "Undock", [session.Pilot.ShipId]);
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
            moving.CallWire("beyonce", "CmdGotoDirection", [1d, 0d, 0d]);
            clock.Seconds += 3;
            moving.Checkpoint();
            moving.RefreshPeers();
            observer.Updates.Clear();
            observer.RefreshPeers();
            var calls = observer.Updates.Cast<object?[]>().Select(tick => (object?[])tick[1]!).ToList();
            Assert.Equal(
                new object?[] { "SetBallPosition", "GotoDirection", "SetSpeedFraction", "SetBallVelocity" },
                calls.Select(call => call[0])
            );
            Assert.All(calls, call => Assert.Equal(moving.Pilot.ShipId, ((object?[])call[1]!)[0]));
            var sample = Assert.Single(space.Peers(observer.Pilot));
            Assert.True(sample.Pilot.Position!.X > initial.X);
            Assert.True(sample.Velocity.X > 0);
            observer.Updates.Clear();
            moving.CallWire("beyonce", "CmdStop", []);
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
        wing.CallWire("beyonce", "CmdDock", [(long)wing.Pilot.StationId, wing.Pilot.ShipId]);
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
            session.CallWire("ship", "Undock", [session.Pilot.ShipId]);
            session.Snapshot();
            session.CallWire("beyonce", "CmdGotoDirection", [1d, 0d, 0d]);
        }
        clock.Seconds = 100;
        foreach (var session in new[] { pilot, wing })
        {
            session.CallWire("beyonce", "CmdStop", []);
        }
        clock.Seconds = 200;
        foreach (var session in new[] { pilot, wing })
        {
            session.Checkpoint();
            session.RefreshPeers();
        }
        Assert.Single(space.Peers(wing.Pilot));
        var departure = pilot.Pilot.Position;
        pilot.Updates.Clear();
        Assert.Null(pilot.CallWire("beyonce", "CmdWarpToStuff", toGate, new() { ["minRange"] = 10_000L }));
        var commands = pilot.Updates.Cast<object?[]>().Select(tick => (object?[])tick[1]!).ToArray();
        Assert.Equal(new[] { "SetSpeedFraction", "SetBallVelocity", "WarpTo", "OnSpecialFX" }, commands.Select(call => call[0]));
        Assert.Equal(3000, ((object?[])commands[2][1]!)[5]);
        int initialUpdates = pilot.Updates.Count;
        Assert.Null(pilot.CallWire("beyonce", "CmdWarpToStuff", toGate, new() { ["minRange"] = 10_000L }));
        Assert.Equal(initialUpdates, pilot.Updates.Count);
        clock.Seconds += 4;
        pilot.Checkpoint();
        Assert.NotEqual(departure, pilot.Pilot.Position);
        pilot.Updates.Clear();
        clock.Seconds += 5;
        pilot.Checkpoint();
        Assert.Empty(pilot.Updates);
        Assert.Null(pilot.CallWire("beyonce", "CmdWarpToStuff", toGate, new() { ["minRange"] = 10_000L }));
        Assert.Empty(pilot.Updates);
        Assert.Same(PilotCallRouter.Unsupported, pilot.CallWire("beyonce", "CmdWarpToStuff", toGate, new() { ["minRange"] = 0L }));
        Assert.Same(PilotCallRouter.Unsupported, pilot.CallWire("beyonce", "CmdStop", []));
        clock.Seconds += 15;
        pilot.Checkpoint();
        pilot.RefreshPeers();
        Assert.Empty(space.Peers(wing.Pilot));
        Assert.True(pilot.Moving);
        Assert.Equal(pilot.Pilot.Position, db.Store.FindPilot(1000001)!.Position);
        clock.Seconds += 120;
        pilot.Checkpoint();
        pilot.RefreshPeers();
        Assert.False(pilot.Moving);
        Assert.InRange(pilot.Pilot.Position!.DistanceTo(gate.Position) - gate.Radius - pilot.Pilot.Flight.ShipRadius, 10_000, 10_550);
        Assert.Same(PilotCallRouter.Unsupported, pilot.CallWire("beyonce", "CmdWarpToStuff", toGate));
        Assert.Null(wing.CallWire("beyonce", "CmdWarpToStuff", toGate));
        clock.Seconds += 8;
        wing.Checkpoint();
        clock.Seconds += 120;
        wing.Checkpoint();
        wing.RefreshPeers();
        Assert.Equal(pilot.Pilot, Assert.Single(space.Peers(wing.Pilot)).Pilot);
        Assert.Equal(wing.Pilot, new SqliteWorld(db.Path).FindPilot(1000002));
        Assert.Null(pilot.CallWire("beyonce", "CmdWarpToStuff", ["item", (long)pilot.Pilot.StationId]));
        clock.Seconds += 8;
        pilot.Checkpoint();
        clock.Seconds += 320;
        pilot.Checkpoint();
        Assert.Null(pilot.CallWire("beyonce", "CmdDock", [(long)pilot.Pilot.StationId, pilot.Pilot.ShipId]));
    }

    [Theory]
    [InlineData(40009098, 7, -639929607985, -111789387758, -1118379774140, 57970000, 61571564.109, -7889581.898, -62815244.816)]
    [InlineData(40009114, 8, -632088182700, -110419568139, -1126786979180, 1100000, -5634213.245, 1422500, -2337871.064)]
    public void CelestialLandingsMatchPublishedFormulasFromEitherSide(
        int id,
        int group,
        double x,
        double y,
        double z,
        float radius,
        double dx,
        double dy,
        double dz
    )
    {
        SpacePosition center = new(x, y, z);
        StaticSpaceObject body = new(id, 0, group, 2, 1, center, radius);
        foreach (double side in new[] { -1d, 1d })
        {
            var anchor = CelestialWarpPoint.Resolve(body, center + new SpacePosition(side * radius, 0, 0));
            Assert.InRange(anchor.DistanceTo(center + new SpacePosition(dx, dy, dz)), 0, 0.002);
        }
    }

    [Theory]
    [InlineData(200000d)]
    [InlineData(4e12)]
    public void WarpExitUsesFirstTickBelowThresholdAndCarriesItsActualVelocity(double length)
    {
        FlightProperties hull = new(100000, 47, 1163000, 4.5f, 295, 3);
        WarpCourse course = new(SpacePosition.Zero, new(length, 0, 0), 40, hull, 230);
        int tick = 40;
        while (!course.Sample(tick, 100).Exited && tick < 200)
        {
            tick++;
        }
        var exit = course.Sample(tick, 100);
        Assert.True(exit.Exited);
        var prior = course.Sample(tick - 1, 100);
        Assert.True(prior.Velocity.X >= 100);
        Assert.InRange(exit.Velocity.X, 0.1, 99.9999);
        Assert.Equal(prior.Velocity.X / Math.E, exit.Velocity.X, 6);
        Assert.Equal(length + 1 - exit.Velocity.X, exit.Position.X, 3);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(-1, 12)]
    public void WarpWaitsForActualAlignment(int heading, int expectedTicks)
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)! with { Position = SpacePosition.Zero };
        StaticSpaceObject gate = new(1, 16, 10, 2, 1, new(1e12, 0, 0), 1000);
        Clock clock = new();
        SharedSpace space = new();
        using FlightSession session = new(db.Store, pilot, clock, space, [gate]);
        session.Snapshot();
        session.CallWire("beyonce", "CmdGotoDirection", [(double)heading, 0d, 0d]);
        clock.Seconds = 60;
        session.CallWire("beyonce", "CmdWarpToStuff", ["item", 1L]);
        session.Updates.Clear();
        clock.Seconds += expectedTicks - 1;
        session.Checkpoint();
        session.RefreshPeers();
        Assert.Null(Assert.Single(space.Peers(pilot with { ShipId = 9 })).Warp);
        clock.Seconds++;
        session.Checkpoint();
        session.RefreshPeers();
        Assert.NotNull(Assert.Single(space.Peers(pilot with { ShipId = 9 })).Warp);
    }

    [Fact]
    public void ObstructedAlignCanBeCancelledAndOnlyForcesWarpAfterTimeout()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)! with { Position = SpacePosition.Zero };
        StaticSpaceObject wall = new(8, 0, 15, 3, 1, new(150, 0, 0), 100);
        StaticSpaceObject gate = new(9, 16, 10, 2, 1, new(1e12, 0, 0), 1000);
        Clock clock = new();
        SharedSpace space = new();
        using FlightSession session = new(db.Store, pilot, clock, space, [wall, gate]);
        session.Snapshot();
        session.CallWire("beyonce", "CmdWarpToStuff", ["item", 9L]);
        clock.Seconds = 12;
        Assert.Null(session.CallWire("beyonce", "CmdStop", []));
        Assert.Equal(3d, session.Pilot.Position!.X, 8);
        session.CallWire("beyonce", "CmdWarpToStuff", ["item", 9L]);
        session.Updates.Clear();
        clock.Seconds += 180;
        session.Checkpoint();
        session.RefreshPeers();
        Assert.Null(Assert.Single(space.Peers(pilot with { ShipId = 9 })).Warp);
        clock.Seconds++;
        session.Checkpoint();
        session.RefreshPeers();
        Assert.NotNull(Assert.Single(space.Peers(pilot with { ShipId = 9 })).Warp);
    }

    [Fact]
    public void LateObserverAndPilotRefreshResumeOriginalWarpDuringDeceleration()
    {
        using TestWorld db = new();
        StaticSpaceObject gate = new(1, 16, 10, 2, 1, new(1e12, 0, 0), 1000);
        var pilot = db.Store.FindPilot(1000001)! with { Position = SpacePosition.Zero };
        var wing = db.Store.FindPilot(1000002)! with { Position = gate.Position + new SpacePosition(0, 5000, 0) };
        Clock clock = new();
        SharedSpace space = new();
        using FlightSession mover = new(db.Store, pilot, clock, space, [gate]);
        mover.Snapshot();
        mover.CallWire("beyonce", "CmdWarpToStuff", ["item", 1L]);
        clock.Seconds = 12;
        using FlightSession observer = new(db.Store, wing, clock, space, [gate]);
        observer.Snapshot();
        observer.Updates.Clear();
        for (; clock.Seconds < 80; clock.Seconds++)
        {
            mover.Checkpoint();
            mover.RefreshPeers();
            observer.Checkpoint();
            observer.RefreshPeers();
            if (observer.Updates.Count > 0)
            {
                break;
            }
        }
        var add = (object?[])((object?[])Assert.Single(observer.Updates)!)[1]!;
        Assert.Equal("AddBalls", add[0]);
        var chunk = (object?[])((object?[])add[1]!)[0]!;
        using BinaryReader reader = new(new MemoryStream((byte[])chunk[0]!));
        reader.ReadByte();
        int current = reader.ReadInt32();
        long shipId = reader.ReadInt64();
        Assert.Equal(pilot.ShipId, shipId);
        Assert.Equal(3, reader.ReadByte());
        reader.ReadSingle();
        Assert.Equal(mover.Pilot.Position, ReadPosition(reader));
        Assert.Equal(9, reader.ReadByte());
        reader.BaseStream.Position += 8 + 1 + 8 + 4 + 4 + 4;
        var velocity = ReadPosition(reader);
        Assert.True(velocity.X > 100);
        reader.BaseStream.Position += 4 + 4 + 1;
        var destination = ReadPosition(reader);
        Assert.Equal(10, reader.ReadInt32());
        Assert.True(current > 10);
        Assert.True(reader.ReadDouble() > 9e11);
        Assert.Equal(0, reader.ReadDouble());
        Assert.Equal(3000, reader.ReadInt64());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        mover.Updates.Clear();
        mover.Snapshot();
        var call = (object?[])((object?[])mover.Updates[0]!)[1]!;
        var state = (BlueInstance)((object?[])call[1]!)[0]!;
        var stream = (byte[])((Dictionary<string, object?>)state.State!)["state"]!;
        Assert.Equal(((byte[])chunk[0]!)[5..], stream.AsSpan(5 + 39, ((byte[])chunk[0]!).Length - 5).ToArray());
        Assert.True(destination.X < gate.Position.X);
        Assert.DoesNotContain(mover.Updates.Concat(observer.Updates), update => Equals(((object?[])((object?[])update!)[1]!)[0], "WarpTo"));
    }

    [Fact]
    public void DelayedCheckpointStopsAtFirstRemoteBodyAndPersistsContact()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)! with { Position = SpacePosition.Zero };
        StaticSpaceObject farther = new(1, 16, 10, 2, 1, new(290, 0, 0), 20);
        var nearer = farther with { Id = 2, Position = new(220, 0, 0) };
        Clock clock = new();
        using FlightSession session = new(db.Store, pilot, clock, new(), [farther, nearer]);
        session.CallWire("beyonce", "CmdGotoDirection", [1d, 0d, 0d]);
        clock.Seconds = 120;
        session.Checkpoint();
        Assert.Equal(153, session.Pilot.Position!.X, 8);
        Assert.False(session.Moving);
        Assert.Equal(session.Pilot.Position, new SqliteWorld(db.Path).FindPilot(1000001)!.Position);
    }

    [Fact]
    public void VerticalStopDampsOncePerTickAndObliqueFollowKeepsMomentum()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        var start = new SpacePosition(pilot.X + pilot.Flight.StationRadius + 2500, pilot.Y, pilot.Z);
        pilot = pilot with { Position = start };
        Clock clock = new();
        SharedSpace space = new();
        using FlightSession session = new(db.Store, pilot, clock, space, db.Objects);
        session.CallWire("beyonce", "CmdGotoDirection", [1d, 1d, 0d]);
        clock.Seconds = 8;
        session.CallWire("beyonce", "CmdStop", []);
        session.RefreshPeers();
        var viewer = pilot with { ShipId = 9 };
        var before = Assert.Single(space.Peers(viewer)).Velocity;
        clock.Seconds += 2;
        session.Checkpoint();
        session.RefreshPeers();
        var after = Assert.Single(space.Peers(viewer)).Velocity;
        Assert.Equal(Math.Exp(-2 / pilot.Flight.InertiaTime), after.X / before.X, 10);
        Assert.Equal(Math.Pow(0.93 / 1.07, 2), after.Y / after.X, 10);
        double range =
            session.Pilot.Position!.DistanceTo(new(pilot.X, pilot.Y, pilot.Z)) - pilot.Flight.StationRadius - pilot.Flight.ShipRadius;
        session.CallWire("beyonce", "CmdFollowBall", [(long)pilot.StationId, range]);
        var atRange = session.Pilot.Position;
        clock.Seconds++;
        session.Checkpoint();
        Assert.True(session.Pilot.Position!.Y > atRange.Y);
        Assert.True(session.Moving);
    }

    [Fact]
    public void WarpRequiresAValidTargetRangeAndInSpaceShip()
    {
        using TestWorld db = new();
        using FlightSession session = new(db.Store, db.Store.FindPilot(1000001)!, new Clock(), new(), db.Objects);
        object?[] target = ["item", (long)db.Objects.Single(body => body.DestinationGateId.HasValue).Id];
        Assert.Same(PilotCallRouter.Unsupported, session.CallWire("beyonce", "CmdWarpToStuff", target));
        session.CallWire("ship", "Undock", [session.Pilot.ShipId]);
        session.Snapshot();
        var origin = session.Pilot.Position;
        Assert.Same(PilotCallRouter.Unsupported, session.CallWire("beyonce", "CmdWarpToStuff", ["item", 1L]));
        Assert.Same(PilotCallRouter.Unsupported, session.CallWire("beyonce", "CmdWarpToStuff", ["bookmark", target[1]]));
        foreach (object range in new object[] { -1L, 100_001L, double.NaN, "bad" })
        {
            Assert.Same(PilotCallRouter.Unsupported, session.CallWire("beyonce", "CmdWarpToStuff", target, new() { ["minRange"] = range }));
        }
        Assert.Equal(origin, session.Pilot.Position);
        Assert.False(session.Moving);
    }

    [Fact]
    public void GateJumpUsesPairedGatesAndRestoresTheDestinationAfterRestart()
    {
        using TestWorld db = new();
        var systems = db.Services.GetRequiredService<ISystemObjects>();
        var gate = db.Objects.Single(o => o.DestinationGateId.HasValue);
        var pilot = db.Store.FindPilot(1000001)!;
        object?[] args = [(long)gate.Id, (long)gate.DestinationGateId!.Value, pilot.ShipId];
        Clock clock = new();
        using FlightSession session = new(db.Store, pilot, clock, new(), db.Objects);
        session.CallWire("ship", "Undock", [pilot.ShipId]);
        session.Snapshot();
        Assert.Same(PilotCallRouter.Unsupported, session.StartJump(args, systems));
        Assert.Null(session.CallWire("beyonce", "CmdWarpToStuff", ["item", (long)gate.Id]));
        clock.Seconds += 360;
        session.Checkpoint();
        Assert.InRange(session.Pilot.Position!.DistanceTo(gate.Position) - gate.Radius, 0, 2547);
        Assert.Same(PilotCallRouter.Unsupported, session.StartJump([args[0], args[1], 1L], systems));
        Assert.Same(PilotCallRouter.Unsupported, session.StartJump([args[0], 1L, args[2]], systems));
        session.Updates.Clear();
        Assert.Null(session.StartJump(args, systems));
        Assert.Equal(
            new object?[]
            {
                pilot.ShipId,
                null,
                null,
                gate.Id,
                null,
                "effects.JumpOut",
                false,
                true,
                false,
                10000,
                0,
                null,
                0,
                new object?[] { gate.DestinationSystemId },
            },
            EffectArguments(session).Single()
        );
        Assert.Same(PilotCallRouter.Unsupported, session.StartJump(args, systems));
        Assert.False(session.CompleteJump());
        clock.Seconds += 10;
        Assert.True(session.CompleteJump());
        Assert.False(session.InBallpark);
        Assert.Equal(gate.DestinationSystemId, session.Pilot.SystemId);
        Assert.Equal(pilot.HomeSystemId, session.Pilot.HomeSystemId);
        Assert.Equal(session.Pilot, new SqliteWorld(db.Path).FindPilot(1000001));
        session.Updates.Clear();
        session.Snapshot();
        Assert.Equal(new[] { "effects.JumpIn" }, EffectArguments(session).Select(a => a[5]));
        Assert.Null(session.CallWire("beyonce", "CmdFollowBall", [args[1], 1000L]));
        clock.Seconds += 90;
        session.Checkpoint();
        Assert.Null(session.StartJump([args[1], args[0], args[2]], systems));
        clock.Seconds += 10;
        Assert.True(session.CompleteJump());
        Assert.Equal(pilot.SystemId, session.Pilot.SystemId);
        Assert.Equal(session.Pilot, db.Store.FindPilot(1000001));

        static IEnumerable<object?[]> EffectArguments(FlightSession flight) =>
            flight
                .Updates.Cast<object?[]>()
                .Select(update => (object?[])update[1]!)
                .Where(call => Equals(call[0], "OnSpecialFX"))
                .Select(call => (object?[])call[1]!);
    }

    [Fact]
    public void StaticObjectsAreScopedToTheSystemAndIncludeClientOnlyBodies()
    {
        using TestWorld db = new();
        var objects = db.Services.GetRequiredService<ISystemObjects>();
        Assert.Equal(new long[] { 40009087, 50001249, 60003760 }, objects.Load(30000142).Select(body => body.Id));
        var other = objects.Load(30000144);
        Assert.Equal(new long[] { 50002185, 40455847, 50016295 }, other.Select(body => body.Id));
        Assert.Equal(new SpacePosition(1, 2, 3), other[1].Position);
        Assert.Equal(10000f, other[1].Radius);
        Assert.Null(other[2].DestinationGateId);
        Assert.Empty(objects.Load(1));
    }

    [Fact]
    public void LongInwardCourseStopsAtTheNearStationSurface()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        Clock clock = new();
        using FlightSession session = new(db.Store, pilot, clock, new(), db.Objects);
        session.CallWire("ship", "Undock", [pilot.ShipId]);
        session.CallWire("beyonce", "CmdGotoDirection", [-1d, 0d, 0d]);
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
        Assert.Null(session.CallWire("ship", "Undock", [pilot.ShipId, false]));
        var spawn = Assert.IsType<SpacePosition>(session.Pilot.Position);
        Assert.Equal(pilot.SystemId, session.Pilot.LocationId);
        Assert.Null(session.CallWire("beyonce", "CmdGotoDirection", [0d, 2d, 0d]));
        clock.Seconds = 3;
        session.Updates.Clear();
        Assert.Null(session.CallWire("beyonce", "CmdStop", []));
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
        Assert.Null(resumed.CallWire("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Equal(pilot, new SqliteWorld(db.Path).FindPilot(1000001));
    }

    [Fact]
    public void SnapshotCarriesTheOwnedShipAndStationAtTheirSavedCoordinates()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        SpacePosition position = new(pilot.X + 123456, pilot.Y - 590, pilot.Z + 200);
        var snapshot = Ballpark.Snapshot(new(pilot with { Position = position }, SpacePosition.Zero, null, 0, null), 42, db.Objects);
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

    private static SpacePosition ReadPosition(BinaryReader reader) => new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());

    [Fact]
    public void ApproachReachesStationAndChecksActiveShipBeforeDocking()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        Clock clock = new();
        FlightSession session = new(db.Store, pilot, clock, new(), db.Objects);
        session.CallWire("ship", "Undock", [pilot.ShipId]);
        session.CallWire("beyonce", "CmdGotoDirection", [1d, 0d, 0d]);
        clock.Seconds = 60;
        Assert.Same(PilotCallRouter.Unsupported, session.CallWire("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Null(session.CallWire("beyonce", "CmdFollowBall", [(long)pilot.StationId, 0d]));
        clock.Seconds = 180;
        session.Checkpoint();
        Assert.False(session.Moving);
        Assert.Same(PilotCallRouter.Unsupported, session.CallWire("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId + 1]));
        Assert.Null(session.CallWire("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
    }

    [Fact]
    public void RejectsWrongShipInvalidCourseAndRemoteDock()
    {
        using TestWorld db = new();
        DockedPilot pilot = db.Store.FindPilot(1000001)!;
        Clock clock = new();
        FlightSession session = new(db.Store, pilot, clock, new(), db.Objects);
        Assert.Same(PilotCallRouter.Unsupported, session.CallWire("ship", "Undock", [pilot.ShipId + 1]));
        Assert.Null(session.CallWire("ship", "Undock", [pilot.ShipId]));
        Assert.Same(PilotCallRouter.Unsupported, session.CallWire("beyonce", "CmdGotoDirection", [double.NaN, 0d, 0d]));
        Assert.Same(PilotCallRouter.Unsupported, session.CallWire("beyonce", "CmdDock", [(long)pilot.StationId + 1, pilot.ShipId]));
        Assert.Null(session.CallWire("beyonce", "CmdGotoDirection", [1d, 0d, 0d]));
        clock.Seconds = 60;
        Assert.Same(PilotCallRouter.Unsupported, session.CallWire("beyonce", "CmdDock", [(long)pilot.StationId, pilot.ShipId]));
        Assert.Equal(pilot.SystemId, db.Store.FindPilot(1000001)!.LocationId);
    }
}
