using Eden.Gameplay.Configuration;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed class AutoNavigationTests
{
    [Fact]
    public void AlignRequestUsesExclusiveNativeKeywordsAndAutopilotUsesOneItemId()
    {
        Assert.Equal(new FlightCommand.AlignTo(42), Read("CmdAlignTo", [], new() { ["dstID"] = 42L, ["bookmarkID"] = null }));
        Assert.Equal(new FlightCommand.AlignToBookmark(7), Read("CmdAlignTo", [], new() { ["dstID"] = null, ["bookmarkID"] = 7L }));
        Assert.Equal(new FlightCommand.AutopilotWarp(42), Read("CmdWarpToStuffAutopilot", [42L]));
        foreach (
            var keywords in new Dictionary<string, object?>[]
            {
                new(),
                new() { ["dstID"] = 42L, ["bookmarkID"] = 7L },
                new() { ["dstID"] = null, ["bookmarkID"] = null },
                new() { ["dstID"] = 42d },
                new() { ["dstID"] = -1L },
                new() { ["dstID"] = 42L, ["x"] = 1L },
            }
        )
        {
            Assert.Null(Read("CmdAlignTo", [], keywords));
        }
        Assert.Null(Read("CmdAlignTo", [42L], new() { ["dstID"] = 42L }));
        Assert.Null(Read("CmdWarpToStuffAutopilot", [42d]));
        Assert.Null(Read("CmdWarpToStuffAutopilot", [42L], new() { ["minRange"] = 0L }));

        static FlightCommand? Read(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            FlightRequestWire.Read("beyonce", method, args, keywords).Command;
    }

    [Fact]
    public void AlignRulesAccelerateAtFullSpeedWithoutWarpAndRejectInvalidPointsWithoutChangingState()
    {
        NavigationState navigation = new();
        Assert.True(navigation.SetHeading(-1, 0, 0));
        Assert.True(navigation.SetSpeed(0.25));
        navigation.SetVelocity(new(-50, 0, 0));
        Assert.True(navigation.AlignTo(SpacePosition.Zero, new(100, 0, 0)));
        Assert.Equal(new SpacePosition(1, 0, 0), navigation.Direction);
        Assert.Equal(1, navigation.SpeedFraction);
        Assert.Equal(new SpacePosition(-50, 0, 0), navigation.Velocity);
        Assert.Null(navigation.WarpDestination);
        Assert.Null(navigation.Warp);
        var frame = navigation.Capture();
        foreach (var invalid in new[] { SpacePosition.Zero, new(double.NaN, 0, 0), new(double.PositiveInfinity, 0, 0) })
        {
            Assert.False(navigation.AlignTo(SpacePosition.Zero, invalid));
            Assert.Equal(frame, navigation.Capture());
        }
    }

    [Fact]
    public void PrealignedShipEntersWarpOnTheNextTickWithoutResettingItsVelocity()
    {
        FlightProperties hull = new(0, 50, 1_000_000, 1, 300, 3);
        NavigationState navigation = new();
        SpacePosition position = SpacePosition.Zero;
        SpacePosition target = new(1_000_000_000, 0, 0);
        Assert.True(navigation.AlignTo(position, target));
        for (int stamp = 1; stamp <= 10; stamp++)
        {
            position = navigation.Advance(1, stamp, hull, position, _ => null, []).Position;
        }
        Assert.Null(navigation.Warp);
        Assert.True(navigation.Velocity.X >= hull.MaxSpeed * 0.75);
        var velocity = navigation.Velocity;
        Assert.True(navigation.StartWarpPoint(1, hull, position, target, 2, 0, 10));
        Assert.Equal(velocity, navigation.Velocity);
        navigation.Advance(1, 11, hull, position, _ => null, []);
        Assert.NotNull(navigation.Warp);
    }

    [Fact]
    public void AlignToDistantGatePublishesHeadingToObserversCanStopAndThenWarp()
    {
        using Fixture fixture = new(clearStation: true);
        var flight = fixture.Context.Flight;
        var gate = fixture.Db.Objects.Single(body => body.DestinationGateId.HasValue);
        var start = flight.Pilot.Position!;
        Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("CmdAlignTo", [], new() { ["dstID"] = flight.Pilot.ShipId }));
        Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("CmdAlignTo", [], new() { ["dstID"] = long.MaxValue }));
        Assert.Null(fixture.Reply("CmdSetSpeedFraction", [0.25]));
        flight.Updates.Clear();
        Assert.Null(fixture.Reply("CmdAlignTo", [], new() { ["dstID"] = gate.Id, ["bookmarkID"] = null }));
        Assert.Collection(
            flight.Updates,
            update => Assert.IsType<DestinyCommand.GotoDirection>(update.Command),
            update => Assert.Equal(new DestinyCommand.SetSpeedFraction(flight.Pilot.ShipId, 1), update.Command)
        );
        var observer = fixture.Db.Store.FindPilot(1000002)! with { Position = start };
        var contact = Assert.Single(fixture.Space.Peers(observer));
        Assert.InRange((gate.Position - start).Unit.DistanceTo(contact.Heading!), 0, 1e-12);
        Assert.Equal(1, contact.SpeedFraction);
        Assert.Null(contact.AlignTo);
        fixture.Advance(30);
        Assert.False(flight.InWarp);
        Assert.False(flight.PreparingWarp);
        Assert.True(flight.Velocity.Unit.Dot((gate.Position - start).Unit) > 0.999);
        Assert.Null(fixture.Reply("CmdStop", []));
        fixture.Advance(60);
        Assert.False(flight.Moving);
        Assert.Null(fixture.Reply("CmdWarpToStuff", ["item", gate.Id]));
        fixture.Advance(360);
        Assert.False(flight.Moving);
        Assert.Equal(flight.Pilot.Position, fixture.Db.Store.FindPilot(1000001)!.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15000)]
    [InlineData(100000)]
    public void AutopilotWarpApproachJumpReturnAndDockUseConfiguredGateRangeAndPersist(int landingMeters)
    {
        using Fixture fixture = new(landingMeters);
        var flight = fixture.Context.Flight;
        var gate = fixture.Db.Objects.Single(body => body.DestinationGateId.HasValue);
        var home = flight.Pilot;
        var departure = home.Position;
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("CmdWarpToStuffAutopilot", [long.MaxValue]));
        Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("CmdWarpToStuffAutopilot", [gate.Id], new() { ["minRange"] = 0L }));
        Assert.Equal(departure, flight.Pilot.Position);
        Assert.False(flight.PreparingWarp);
        Assert.Null(fixture.Reply("CmdWarpToStuffAutopilot", [gate.Id]));
        flight.Updates.Clear();
        Assert.Null(fixture.Reply("CmdWarpToStuffAutopilot", [gate.Id]));
        Assert.Empty(flight.Updates);
        fixture.Advance(360);
        Assert.False(flight.Moving);
        double distance = flight.Pilot.Position!.DistanceTo(gate.Position) - gate.Radius - flight.Pilot.Flight.ShipRadius;
        Assert.InRange(distance, landingMeters, landingMeters + 600);
        if (landingMeters > GateTravelRules.ActivationRange)
        {
            Assert.IsType<PilotCallResult.UnsupportedCall>(
                fixture.Call("CmdStargateJump", [gate.Id, (long)gate.DestinationGateId!, home.ShipId])
            );
        }
        Assert.Null(fixture.Reply("CmdFollowBall", [gate.Id, 0d]));
        fixture.Advance(1000);
        Jump(gate.Id, gate.DestinationGateId!.Value);
        Assert.Equal(gate.DestinationSystemId, flight.Pilot.SystemId);
        Assert.Equal(flight.Pilot.SystemId, WorldInitialization.Open(fixture.Db.Path).FindPilot(1000001)!.SystemId);
        Assert.Null(fixture.Reply("CmdFollowBall", [(long)gate.DestinationGateId.Value, 0d]));
        fixture.Advance(120);
        Jump(gate.DestinationGateId.Value, gate.Id);
        Assert.Equal(home.SystemId, flight.Pilot.SystemId);
        Assert.Null(fixture.Reply("CmdWarpToStuffAutopilot", [(long)home.StationId]));
        fixture.Advance(360);
        var station = fixture.Db.Objects.Single(body => body.Id == home.StationId);
        Assert.InRange(flight.Pilot.Position!.DistanceTo(station.Position) - station.Radius - home.Flight.ShipRadius, 15000, 15600);
        Assert.Null(fixture.Reply("CmdFollowBall", [(long)home.StationId, 0d]));
        fixture.Advance(180);
        Assert.Null(fixture.Reply("CmdDock", [(long)home.StationId, home.ShipId]));
        Assert.Null(flight.Pilot.Position);
        Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("CmdAlignTo", [], new() { ["dstID"] = gate.Id }));
        Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("CmdWarpToStuffAutopilot", [gate.Id]));
        Assert.Equal(home.StationId, WorldInitialization.Open(fixture.Db.Path).FindPilot(1000001)!.StationId);

        void Jump(long source, long exit)
        {
            Assert.Null(fixture.Reply("CmdStargateJump", [source, exit, home.ShipId]));
            Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("CmdStargateJump", [source, exit, home.ShipId]));
            flight.GateDeparturePublished();
            fixture.Clock.Seconds += FlightSession.JumpDurationTicks;
            Assert.True(flight.CompleteJump());
            Assert.False(flight.CompleteJump());
            flight.Snapshot();
        }
    }

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = new();
        public SharedSpace Space { get; } = new();
        public TestWorld Db { get; }
        public PilotCallContext Context { get; }
        private readonly IServiceScope _scope;

        public Fixture(int? landingMeters = null, bool clearStation = false)
        {
            Db = new(clock: Clock);
            if (landingMeters is { } distance)
            {
                var configuration = Db.Services.GetRequiredService<IWorldConfiguration>();
                var state = configuration.Read();
                configuration.Save(
                    state.Generation,
                    state.Active.Draft with
                    {
                        Settings = state.Active.Draft.Settings with { Navigation = new(distance) },
                    },
                    "Autopilot test"
                );
                WorldInitialization.Initialize(new(Db.Path));
            }
            _scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(
                1000001,
                pilot.Account,
                [pilot],
                p =>
                    new(
                        new Positions(Db.Store),
                        p,
                        Clock,
                        Space,
                        Db.Objects,
                        docking: Db.Services.GetRequiredService<IStationDocking>(),
                        gates: Db.Services.GetRequiredService<IPilotGateTravel>()
                    ),
                42,
                SignedSettings.Empty
            );
            if (clearStation)
            {
                Db.Store.SetPosition(pilot.ShipId, pilot.Home.Position + new SpacePosition(1_000_000_000, 0, 0));
                pilot = Db.Store.FindPilot(1000001)!;
            }
            Context.Select(pilot);
            if (!clearStation)
            {
                Reply("Undock", [pilot.ShipId], service: "ship");
            }
            Context.Flight.Snapshot();
            Context.Flight.Updates.Clear();
        }

        public void Advance(int seconds)
        {
            Clock.Seconds += seconds;
            Context.Flight.Checkpoint();
        }

        public PilotCallResult Call(
            string method,
            object?[] args,
            Dictionary<string, object?>? keywords = null,
            string service = "beyonce"
        ) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public object? Reply(string method, object?[] args, Dictionary<string, object?>? keywords = null, string service = "beyonce") =>
            BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(Call(method, args, keywords, service)).Reply));

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }

    // Exercise durable positions, gates and docking without SQL on each catch-up tick.
    private sealed class Positions(IShipPositionStore store) : IShipPositionStore
    {
        public void SetPosition(long shipId, SpacePosition? position, int? systemId = null) =>
            store.SetPosition(shipId, position, systemId);
    }
}
