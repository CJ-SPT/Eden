using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Protocol.Tests.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed class ShipCollisionTests
{
    private static readonly FlightProperties Ibis = new(0, 47, 1_163_000, 4.5f, 295, 3);

    private static CollisionMotion Coast(long id, SpacePosition start, SpacePosition velocity, FlightProperties? hull = null)
    {
        hull ??= Ibis;
        double decay = Math.Exp(-1 / hull.InertiaTime);
        return new(id, hull, start, velocity, start + velocity * (hull.InertiaTime * (1 - decay)), velocity * decay);
    }

    [Fact]
    public void FixedBodyReflectsRadialVelocityUsingCarbonsDampedImpulse()
    {
        var moving = Coast(1, SpacePosition.Zero, new(200, 0, 0));
        StaticSpaceObject station = new(2, 0, 15, 3, 1, new(200, 0, 0), Ibis.ShipRadius);
        var result = ShipCollisions.ResolveStatic(moving, [station]);
        double time = (200 - 2 * Ibis.ShipRadius) / moving.Position.X;
        double impactSpeed = 200 * Math.Exp(-time / Ibis.InertiaTime);
        double endImpulse = 2 * impactSpeed * Math.Exp(-(1 - time) / Ibis.InertiaTime) * 0.85;
        Assert.True(result.Bumped);
        Assert.Equal(moving.Velocity.X - endImpulse, result.Velocity.X, 8);
        Assert.Equal(0, result.Velocity.Y);
        Assert.Equal(0, result.Velocity.Z);
        Assert.True(result.Position.X < moving.Position.X);
    }

    [Fact]
    public void GlancingFixedContactRetainsTheHeadingThrottleAndTangentialMovement()
    {
        NavigationState navigation = new();
        navigation.SetHeading(1, 1, 0);
        navigation.SetVelocity(new(200, 200, 0));
        var heading = navigation.Direction;
        StaticSpaceObject station = new(2, 0, 15, 3, 1, new(150, 200, 0), 50);
        var tick = navigation.Advance(1, 1, Ibis, SpacePosition.Zero, _ => null, [station]);
        Assert.Equal(heading, navigation.Direction);
        Assert.Equal(1, navigation.SpeedFraction);
        Assert.True(navigation.Velocity.X > 0);
        Assert.True(navigation.Velocity.Length > 0);
        Assert.True(tick.Position.X > 0);
        Assert.Empty(tick.Changes);
        var free = new NavigationState();
        free.SetHeading(1, 1, 0);
        free.SetVelocity(new(200, 200, 0));
        var unobstructed = free.Advance(1, 1, Ibis, SpacePosition.Zero, _ => null, []);
        Assert.True(navigation.Velocity.DistanceTo(free.Velocity) > 1);
        Assert.True(tick.Position.DistanceTo(unobstructed.Position) > 0.1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StationBumpsRetainCoursesForBothObserversWithoutSendingStopOrMotionResets(bool observerFirst)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        db.Store.SetPosition(1000000000001, SpacePosition.Zero);
        db.Store.SetPosition(1000000000002, new(0, 10000, 0));
        SharedSpace space = new();
        StaticSpaceObject station = new(9, 0, 15, 3, 1, new(220, 0, 0), 100);
        using FlightSession mover = new(db.Store, db.Store.FindPilot(1000001)!, clock, space, [station]);
        using FlightSession observer = new(db.Store, db.Store.FindPilot(1000002)!, clock, space, [station]);
        mover.Snapshot();
        observer.Snapshot();
        mover.Execute(new FlightCommand.Heading(1, 0, 0));
        bool bounced = false;
        foreach (int tick in Enumerable.Range(1, 30))
        {
            clock.Seconds = tick;
            foreach (var session in observerFirst ? new[] { observer, mover } : new[] { mover, observer })
            {
                session.Updates.Clear();
                session.RefreshPeers();
                Assert.DoesNotContain(
                    session.Updates,
                    update => update.Command is DestinyCommand.Stop or DestinyCommand.SetBallPosition or DestinyCommand.SetBallVelocity
                );
            }
            bounced |= mover.Velocity.X < 0;
        }
        Assert.True(bounced);
        Assert.True(mover.Moving);
        Assert.Equal(mover.Velocity, Assert.Single(space.Peers(observer.Pilot)).Velocity);
        Assert.Equal(mover.Pilot.Position, db.Store.FindPilot(1000001)!.Position);
        mover.Execute(new FlightCommand.Heading(0, 1, 0));
        var contact = mover.Pilot.Position!;
        clock.Seconds += 20;
        mover.Checkpoint();
        Assert.True(mover.Pilot.Position!.Y > contact.Y + 100);
    }

    [Fact]
    public void CarbonHeadOnResponseUsesRawMassAndDampedAcceleration()
    {
        var moving = Coast(1, SpacePosition.Zero, new(200, 0, 0));
        var stopped = Coast(2, new(200, 0, 0), SpacePosition.Zero);
        var results = ShipCollisions.Resolve([moving, stopped]);
        // Carbon Potential solves the full-tick acceleration from the impact velocity,
        // then applies 85 percent of it before Integrate. This is not a hard stop.
        double time = (200 - 94) / moving.Position.X;
        double impactSpeed = 200 * Math.Exp(-time / Ibis.InertiaTime);
        double endImpulse = impactSpeed * Math.Exp(-(1 - time) / Ibis.InertiaTime) * 0.85;
        Assert.Equal(moving.Velocity.X - endImpulse, results[0].Velocity.X, 8);
        Assert.Equal(endImpulse, results[1].Velocity.X, 8);
        Assert.True(results.All(r => r.Bumped));
        Assert.True(results[1].Position.X > stopped.Position.X);
    }

    [Fact]
    public void GlancingContactPreservesTangentialMotionAndIsIndependentOfInputOrder()
    {
        var moving = Coast(1, SpacePosition.Zero, new(200, 25, 0));
        var heavy = Coast(2, new(200, 60, 0), SpacePosition.Zero, Ibis with { Mass = Ibis.Mass * 10 });
        var forward = ShipCollisions.Resolve([moving, heavy]).OrderBy(s => s.Id).ToArray();
        var reverse = ShipCollisions.Resolve([heavy, moving]).OrderBy(s => s.Id).ToArray();
        Assert.Equal(forward, reverse);
        Assert.All(forward, s => Assert.True(s.Bumped));
        Assert.True(forward[0].Velocity.Y < moving.Velocity.Y);
        Assert.True(forward[1].Velocity.Length < forward[0].Velocity.Length);
    }

    [Fact]
    public void CoincidentStationaryShipsSeparateDeterministicallyWithoutChangingTheirCourse()
    {
        var results = ShipCollisions.Resolve([
            Coast(1, new(1e12, 2e12, -3e12), SpacePosition.Zero),
            Coast(2, new(1e12, 2e12, -3e12), SpacePosition.Zero),
        ]);
        Assert.All(results, result => Assert.True(double.IsFinite(result.Position.Length)));
        Assert.True(results[0].Velocity.X > 0);
        Assert.True(results[1].Velocity.X < 0);
        Assert.Equal(0, (results[0].Velocity + results[1].Velocity).Length, 8);
    }

    [Fact]
    public void CloseFlybyAndWarpingShipDoNotBump()
    {
        var moving = Coast(1, SpacePosition.Zero, new(500, 0, 0));
        var flyby = Coast(2, new(200, 95, 0), SpacePosition.Zero);
        Assert.All(ShipCollisions.Resolve([moving, flyby]), s => Assert.False(s.Bumped));
        var warping = flyby with { Start = new(200, 0, 0), Position = new(200, 0, 0), Solid = false };
        Assert.All(ShipCollisions.Resolve([moving, warping]), s => Assert.False(s.Bumped));
    }

    [Fact]
    public void TwoClientsUseTheSameTickEvenWhenTheStoppedViewerAdvancesFirst()
    {
        var forward = RunPair(false);
        var reverse = RunPair(true);
        Assert.Equal(forward, reverse);
    }

    private static SpacePosition[] RunPair(bool reverse)
    {
        using TestWorld db = new();
        TestClock clock = new();
        SharedSpace space = new();
        db.Store.SetPosition(1000000000001, new(1_000_000, 0, 0));
        db.Store.SetPosition(1000000000002, new(1_000_500, 0, 0));
        using FlightSession pilot = new(db.Store, db.Store.FindPilot(1000001)!, clock, space, []);
        using FlightSession wing = new(db.Store, db.Store.FindPilot(1000002)!, clock, space, []);
        pilot.Snapshot();
        wing.Snapshot();
        pilot.Execute(new FlightCommand.Heading(1, 0, 0));
        var order = reverse ? new[] { wing, pilot } : new[] { pilot, wing };
        bool bumped = false;
        for (int second = 1; second <= 30; second++)
        {
            clock.Seconds = second;
            foreach (var session in order)
            {
                session.Updates.Clear();
                session.Checkpoint();
                session.RefreshPeers();
            }
            Assert.Equal(pilot.Pilot.Position, db.Store.FindPilot(1000001)!.Position);
            Assert.Equal(wing.Pilot.Position, db.Store.FindPilot(1000002)!.Position);
            Assert.Equal(pilot.Pilot.Position, Assert.Single(space.Peers(wing.Pilot)).Pilot.Position);
            Assert.Equal(wing.Pilot.Position, Assert.Single(space.Peers(pilot.Pilot)).Pilot.Position);
            var before = wing.Pilot.Position;
            wing.Checkpoint();
            Assert.Equal(before, wing.Pilot.Position);
            bumped |= wing.Velocity.Length > 0;
            Assert.DoesNotContain(pilot.Updates, u => u.Command is DestinyCommand.SetBallVelocity or DestinyCommand.SetBallPosition);
            Assert.DoesNotContain(wing.Updates, u => u.Command is DestinyCommand.SetBallVelocity or DestinyCommand.SetBallPosition);
            Assert.Null(Assert.Single(space.Peers(pilot.Pilot)).Heading);
            Assert.All(pilot.Updates, update => Assert.InRange(update.Stamp, 1, second + 1));
        }
        Assert.True(bumped);
        return [pilot.Pilot.Position!, wing.Pilot.Position!];
    }

    [Fact]
    public void SettledShipsReceiveNoRepeatedPositionOrOrientationResets()
    {
        using TestWorld db = new();
        TestClock clock = new();
        SharedSpace space = new();
        db.Store.SetPosition(1000000000001, new(1_000_000, 0, 0));
        db.Store.SetPosition(1000000000002, new(1_001_500, 0, 0));
        using FlightSession pilot = new(db.Store, db.Store.FindPilot(1000001)!, clock, space, []);
        using FlightSession wing = new(db.Store, db.Store.FindPilot(1000002)!, clock, space, []);
        pilot.Snapshot();
        wing.Snapshot();
        pilot.RefreshPeers();
        for (int second = 1; second <= 25; second++)
        {
            clock.Seconds = second;
            foreach (var session in new[] { pilot, wing })
            {
                session.Updates.Clear();
                session.Checkpoint();
                session.RefreshPeers();
                Assert.DoesNotContain(session.Updates, u => u.Command is DestinyCommand.SetBallVelocity or DestinyCommand.SetBallPosition);
            }
        }
    }

    [Fact]
    public void ObserversReceiveTheOriginalCommandTickWithoutReseedingSkippedSamples()
    {
        using TestWorld db = new();
        TestClock clock = new();
        SharedSpace space = new();
        db.Store.SetPosition(1000000000001, new(1_000_000, 0, 0));
        db.Store.SetPosition(1000000000002, new(1_002_000, 0, 0));
        using FlightSession pilot = new(db.Store, db.Store.FindPilot(1000001)!, clock, space, []);
        using FlightSession wing = new(db.Store, db.Store.FindPilot(1000002)!, clock, space, []);
        pilot.Snapshot();
        wing.Snapshot();
        pilot.RefreshPeers();
        pilot.Updates.Clear();
        wing.Updates.Clear();
        pilot.Execute(new FlightCommand.Heading(1, 0, 0));
        var commands = pilot.Updates.ToArray();
        clock.Seconds = 2;
        wing.RefreshPeers();
        Assert.Equal(commands, wing.Updates);
        Assert.All(wing.Updates, u => Assert.Equal(1, u.Stamp));
        for (int second = 4; second <= 30; second += 2)
        {
            clock.Seconds = second;
            wing.Updates.Clear();
            wing.RefreshPeers();
            Assert.DoesNotContain(wing.Updates, u => u.Command is DestinyCommand.SetBallPosition or DestinyCommand.SetBallVelocity);
        }
    }

    [Fact]
    public void AnOlderConnectionSampleCannotReplaceCommittedMovement()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)! with { Position = new(1_000_000, 0, 0) };
        SharedSpace space = new();
        var old = new ShipContact(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null, Stamp: 1);
        var current = old with { Pilot = old.Pilot with { Position = new(1_000_100, 0, 0) }, Stamp = 2 };
        space.Publish(current);
        space.Publish(old);
        Assert.Equal(current, Assert.Single(space.Peers(pilot with { ShipId = long.MaxValue })));
    }

    [Fact]
    public void DelayedOwnerRecoversTheCurrentSampleAfterHistoryExpires()
    {
        using TestWorld db = new();
        TestClock clock = new();
        SharedSpace space = new();
        db.Store.SetPosition(1000000000001, new(1_000_000, 0, 0));
        db.Store.SetPosition(1000000000002, new(1_010_000, 0, 0));
        using FlightSession pilot = new(db.Store, db.Store.FindPilot(1000001)!, clock, space, []);
        using FlightSession wing = new(db.Store, db.Store.FindPilot(1000002)!, clock, space, []);
        pilot.Snapshot();
        wing.Snapshot();
        pilot.Execute(new FlightCommand.Heading(1, 0, 0));
        for (int second = 1; second <= 45; second++)
        {
            clock.Seconds = second;
            wing.Checkpoint();
        }
        pilot.Updates.Clear();
        pilot.Checkpoint();
        Assert.Equal(Assert.Single(space.Peers(wing.Pilot)).Pilot.Position, pilot.Pilot.Position);
        Assert.Contains(pilot.Updates, u => u.Command is DestinyCommand.SetBallPosition);
        Assert.All(pilot.Updates, u => Assert.Equal(46, u.Stamp));
    }

    [Fact]
    public void SustainedApproachKeepsItsOrderThroughContact()
    {
        using TestWorld db = new();
        TestClock clock = new();
        SharedSpace space = new();
        db.Store.SetPosition(1000000000001, new(1_000_000, 0, 0));
        db.Store.SetPosition(1000000000002, new(1_000_500, 0, 0));
        using FlightSession pilot = new(db.Store, db.Store.FindPilot(1000001)!, clock, space, []);
        using FlightSession wing = new(db.Store, db.Store.FindPilot(1000002)!, clock, space, []);
        pilot.Snapshot();
        wing.Snapshot();
        pilot.Execute(new FlightCommand.Heading(1, 0, 0));
        clock.Seconds = 4;
        pilot.Checkpoint();
        pilot.Execute(new FlightCommand.Follow(wing.Pilot.ShipId, 0, false));
        bool bumped = false;
        for (int second = 5; second <= 30; second++)
        {
            clock.Seconds = second;
            wing.Checkpoint();
            pilot.Checkpoint();
            pilot.RefreshPeers();
            var sample = Assert.Single(space.Peers(wing.Pilot));
            Assert.Equal(wing.Pilot.ShipId, sample.FollowTargetId);
            Assert.Equal(0d, sample.FollowRange);
            bumped |= wing.Velocity.Length > 0;
        }
        Assert.True(bumped);
    }

    [Fact]
    public void PlayerAndNpcBumpsAreCommittedTogetherAndASecondViewerCannotRepeatTheStep()
    {
        using TestWorld db = new();
        TestClock clock = new();
        NpcEncounterTests.Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var contacts = encounters.List(NpcEncounterDefinitions.PerimeterSystem, clock.Now);
        var npc = contacts[0];
        using (WorldDbContext world = new(db.Path))
        {
            var saved = world.Set<WorldNpc>().Find(npc.Id)!;
            (saved.X, saved.Y, saved.Z) = (1_000_050, 0, 0);
            var neighbor = world.Set<WorldNpc>().Find(contacts[1].Id)!;
            (neighbor.X, neighbor.Y, neighbor.Z) = (1_000_060, 0, 0);
            world.SaveChanges();
        }
        db.Store.SetPosition(1000000000001, new(1_000_000, 0, 0), npc.SystemId);
        db.Store.SetPosition(1000000000002, new(1_010_000, 0, 0), npc.SystemId);
        SharedSpace space = new();
        using FlightSession pilot = new(db.Store, db.Store.FindPilot(1000001)!, clock, space, [], encounters: encounters);
        using FlightSession wing = new(db.Store, db.Store.FindPilot(1000002)!, clock, space, [], encounters: encounters);
        pilot.Snapshot();
        wing.Snapshot();
        clock.Seconds = 1;
        pilot.Checkpoint();
        var first = encounters.List(npc.SystemId, clock.Now).Single(n => n.Id == npc.Id);
        wing.Checkpoint();
        var second = encounters.List(npc.SystemId, clock.Now).Single(n => n.Id == npc.Id);
        Assert.Equal(first, second);
        Assert.NotEqual(npc.Position, first.Position);
        Assert.True(first.Navigation!.Velocity.Length > 0);
        Assert.Equal(pilot.Pilot.Position, db.Store.FindPilot(1000001)!.Position);
        Assert.True(first.Bumped);
        Assert.Equal(2, first.Stamp);
        var neighborContact = encounters.List(npc.SystemId, clock.Now).Single(n => n.Id == contacts[1].Id);
        Assert.True(neighborContact.Bumped);
        Assert.True(neighborContact.Navigation!.Velocity.Length > 0);
        pilot.Updates.Clear();
        clock.Seconds = 4;
        pilot.Checkpoint();
        pilot.RefreshNpcs();
        Assert.DoesNotContain(pilot.Updates, u => u.Command is DestinyCommand.SetBallPosition or DestinyCommand.SetBallVelocity);
    }
}
