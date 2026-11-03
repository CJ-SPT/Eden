using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;

namespace Eden.Protocol.Tests.Flight;

public sealed class OrbitTests
{
    private sealed class Clock : TimeProvider
    {
        public long Seconds { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    private sealed class Encounters : INpcEncounters
    {
        public NpcContact[] Contacts { get; set; } = [];
        public IReadOnlyList<NpcContact> List(int systemId, long now) => Contacts;
        public bool WasDestroyed(long npcId) => Contacts.All(n => n.Id != npcId);
    }

    private static object?[][] Actions(FlightSession session) => session.Updates.Cast<object?[]>().Select(u => (object?[])u[1]!).ToArray();

    [Fact]
    public void SteeringMatchesCarbonsPublishedTenTickOrbitTrajectory()
    {
        // Carbon 27ee40b test_orbit.py TestOldOrbit.test_orbit_ball.
        SpacePosition[] expected = [new(5, 0, 0.3946594280372685), new(4.968965087530276, -0.014060509698578017, 1.533749375235321),
            new(4.7948197558341885, -0.09342506603769896, 3.3353413772468783), new(4.303462228867957, -0.3182911817228117, 5.664861650309143),
            new(3.341010762290748, -0.7601346870799338, 8.325917825178466), new(1.826888038965309, -1.4570571575293851, 11.103680716692217),
            new(-0.25072581197636695, -2.415534207332392, 13.808614450861453), new(-2.860769892110699, -3.6221094985835713, 16.289471089613752),
            new(-5.948838546575331, -5.052297043579673, 18.42935795458875), new(-9.447930869309866, -6.675507070626672, 20.138672333159825)];
        StaticSpaceObject target = new(1, 0, 0, 0, 0, SpacePosition.Zero, 2);
        SpacePosition position = new(5, 0, 0), velocity = SpacePosition.Zero;
        double tau = 13 * (double)0.9f, decay = Math.Exp(-1 / tau);
        for (int stamp = 0; stamp < expected.Length; stamp++)
        {
            var terminalVelocity = OrbitCourse.Direction(2, stamp, position, target, 2, 1) * (10 * (double)0.95f);
            position += terminalVelocity + (velocity - terminalVelocity) * (tau * (1 - decay));
            velocity = terminalVelocity + (velocity - terminalVelocity) * decay;
            Assert.InRange(position.DistanceTo(expected[stamp]), 0, 0.000001);
        }
    }

    [Fact]
    public void OrbitConvergesWithInertiaAndPropulsionChangesThenStopCancelsIt()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)! with { Position = new(1_010_000, 0, 0) };
        StaticSpaceObject target = new(999, 0, 0, 0, 0, new(1_000_000, 0, 0), 100);
        Clock clock = new();
        using FlightSession session = new(db.Store, pilot, clock, new(), [target]);
        session.Snapshot();
        Assert.Null(session.Call("beyonce", "CmdOrbit", [target.Id, 5000L]));
        for (int second = 1; second <= 300; second++)
        {
            clock.Seconds = second;
            session.Checkpoint();
        }
        var radial = session.Pilot.Position! - target.Position;
        Assert.InRange(radial.Length - target.Radius - pilot.Flight.ShipRadius, 4500, 6000);
        Assert.InRange(Math.Abs(radial.Unit.Dot(session.Velocity.Unit)), 0, 0.3);
        var velocity = session.Velocity;
        session.SetPropulsionEffect(new(new(123, (int)PropulsionModuleType.OneMnAfterburnerI, 10000, 20, 115, 1500000, 500000), 0));
        Assert.Equal(velocity, session.Velocity);
        for (int second = 301; second <= 600; second++)
        {
            clock.Seconds = second;
            session.Checkpoint();
        }
        Assert.InRange(session.Pilot.Position!.DistanceTo(target.Position) - target.Radius - pilot.Flight.ShipRadius, 4500, 8500);
        Assert.True(session.Velocity.Length > pilot.Flight.MaxSpeed);
        Assert.Null(session.Call("beyonce", "CmdStop", []));
        session.SetPropulsionEffect(null);
        clock.Seconds = 800;
        session.Checkpoint();
        Assert.False(session.Moving);
    }

    [Fact]
    public void OrbitAndKeepAtRangeHaveDistinctNativeSnapshotsAndObserverCommands()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)! with { Position = new(1_000_000, 0, 0) };
        var wing = db.Store.FindPilot(1000002)! with { Position = new(1_005_000, 0, 0) };
        Clock clock = new();
        SharedSpace space = new();
        using FlightSession session = new(db.Store, pilot, clock, space, []);
        using FlightSession observer = new(db.Store, wing, clock, space, []);
        session.Snapshot();
        observer.Snapshot();
        Assert.Null(session.Call("beyonce", "CmdOrbit", [wing.ShipId, 2500L]));
        session.RefreshPeers();
        observer.Updates.Clear();
        observer.RefreshPeers();
        Assert.Contains(Actions(observer), a => Equals(a[0], "Orbit") && Equals(((object?[])a[1]!)[1], wing.ShipId));
        Assert.DoesNotContain(Actions(observer), a => Equals(a[0], "SetState"));
        var contact = Assert.Single(space.Peers(wing));
        Assert.Equal(DestinyBallMode.Orbit, contact.FollowMode);
        var chunk = Ballpark.AddShip(contact, 42);
        using BinaryReader reader = new(new MemoryStream((byte[])chunk[0]!));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(42, reader.ReadInt32());
        Assert.Equal(pilot.ShipId, reader.ReadInt64());
        Assert.Equal((byte)DestinyBallMode.Orbit, reader.ReadByte());
        reader.ReadBytes(sizeof(float) + 3 * sizeof(double) + sizeof(byte) + sizeof(double) + sizeof(byte) + sizeof(long) +
            2 * sizeof(int) + sizeof(float) + 3 * sizeof(double) + 2 * sizeof(float) + sizeof(sbyte));
        Assert.Equal(wing.ShipId, reader.ReadInt64());
        Assert.Equal(2500, reader.ReadSingle());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        Assert.NotEmpty(BlueMarshal.Encode(chunk));
        Assert.Null(session.Call("beyonce", "CmdFollowBall", [wing.ShipId, 2500L]));
        session.RefreshPeers();
        observer.Updates.Clear();
        observer.RefreshPeers();
        Assert.Contains(Actions(observer), a => Equals(a[0], "FollowBall"));
        Assert.Equal(DestinyBallMode.Follow, Assert.Single(space.Peers(wing)).FollowMode);
    }

    [Theory]
    [InlineData("CmdOrbit", "GotoDirection")]
    [InlineData("CmdFollowBall", "Stop")]
    public void ADepartingPeerCancelsTheCourseWithoutKeepingAStaleTarget(string method, string departureAction)
    {
        using TestWorld db = new();
        Clock clock = new();
        SharedSpace space = new();
        var pilot = db.Store.FindPilot(1000001)! with { Position = new(1_000_000, 0, 0) };
        var wing = db.Store.FindPilot(1000002)! with { Position = new(1_005_000, 0, 0) };
        using FlightSession session = new(db.Store, pilot, clock, space, []);
        using FlightSession target = new(db.Store, wing, clock, space, []);
        session.Snapshot();
        target.Snapshot();
        Assert.Null(session.Call("beyonce", method, [wing.ShipId, 2500L]));
        clock.Seconds = 5;
        session.Checkpoint();
        target.Dispose();
        session.Updates.Clear();
        clock.Seconds++;
        session.Checkpoint();
        Assert.Contains(Actions(session), a => Equals(a[0], departureAction));
        session.RefreshPeers();
        Assert.Null(Assert.Single(space.Peers(wing)).FollowRange);
    }

    [Fact]
    public void MovingNpcUsesItsLatestPositionAndDestructionEndsTheOrbit()
    {
        using TestWorld db = new();
        Clock clock = new();
        var pilot = db.Store.FindPilot(1000001)! with { Position = new(1_006_000, 0, 0) };
        ShipHealth health = new(999, ShipHealth.DefenseFrom(new Dictionary<DogmaAttributeId, double>
        {
            [DogmaAttributeId.ShieldCapacity] = 100,
            [DogmaAttributeId.ArmorHitPoints] = 100,
            [DogmaAttributeId.StructureHitPoints] = 100,
        }), new(100, 100, 100));
        NpcContact npc = new(999, 0, 0, 0, 0, "Moving target", pilot.SystemId, new(1_000_000, 0, 0), 1000000, 40, 220, 1, health);
        Encounters encounters = new() { Contacts = [npc] };
        using FlightSession session = new(db.Store, pilot, clock, new(), [], encounters: encounters);
        session.Snapshot();
        Assert.Null(session.Call("beyonce", "CmdOrbit", [npc.Id, 5000L]));
        clock.Seconds = 1;
        session.Checkpoint();
        npc = npc with { Position = npc.Position + new SpacePosition(0, 3000, 0) };
        encounters.Contacts = [npc];
        session.RefreshNpcs();
        var expectedThrust = OrbitCourse.Direction(pilot.ShipId, 2, session.Pilot.Position!,
            new(npc.Id, npc.TypeId, npc.GroupId, npc.CategoryId, npc.CorporationId, npc.Position, npc.Radius), pilot.Flight.ShipRadius, 5000) * pilot.Flight.MaxSpeed;
        double decay = Math.Exp(-1 / pilot.Flight.InertiaTime);
        var expectedVelocity = expectedThrust + (session.Velocity - expectedThrust) * decay;
        clock.Seconds = 2;
        session.Checkpoint();
        Assert.InRange(session.Velocity.DistanceTo(expectedVelocity), 0, 0.000001);
        encounters.Contacts = [];
        session.RefreshNpcs();
        session.Updates.Clear();
        clock.Seconds++;
        session.Checkpoint();
        Assert.Contains(Actions(session), a => Equals(a[0], "GotoDirection"));
    }

    [Fact]
    public void InvalidTargetsRangesAndWarpNeverCreateAnOrbitAndRestartRetainsOnlyPosition()
    {
        using TestWorld db = new();
        Clock clock = new();
        var pilot = db.Store.FindPilot(1000001)! with { Position = new(1_006_000, 0, 0) };
        StaticSpaceObject target = new(999, 0, 0, 0, 0, new(1_000_000, 0, 0), 40);
        StaticSpaceObject distant = target with { Id = 1000, Position = new(1_000_000_000, 0, 0) };
        SpacePosition saved;
        using (FlightSession session = new(db.Store, pilot, clock, new(), [target, distant]))
        {
            Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdOrbit", [target.Id, 1000L]));
            session.Snapshot();
            foreach (object?[] args in new object?[][] { [pilot.ShipId, 1000L], [1234L, 1000L], [distant.Id, 1000L],
                [target.Id, -1L], [target.Id, double.NaN], [target.Id, double.PositiveInfinity], [target.Id, 1_000_001L] })
            {
                Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdOrbit", args));
            }
            Assert.Null(session.Call("beyonce", "CmdOrbit", [target.Id, 1000L]));
            clock.Seconds = 10;
            session.Checkpoint();
            Assert.Null(session.Call("beyonce", "CmdWarpToStuff", ["item", distant.Id]));
            Assert.Same(PilotCallRouter.Unsupported, session.Call("beyonce", "CmdOrbit", [target.Id, 1000L]));
            Assert.Null(session.Call("beyonce", "CmdStop", []));
            saved = session.Pilot.Position!;
        }
        var reopened = new SqliteWorld(db.Path).FindPilot(1000001)!;
        Assert.Equal(saved, reopened.Position);
        SharedSpace space = new();
        using FlightSession restarted = new(db.Store, reopened, clock, space, [target]);
        restarted.Snapshot();
        Assert.False(restarted.Moving);
        Assert.Null(Assert.Single(space.Peers(pilot with { ShipId = long.MaxValue })).FollowRange);
    }
}
