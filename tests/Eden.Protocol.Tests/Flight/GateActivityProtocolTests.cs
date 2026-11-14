using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed class GateActivityProtocolTests
{
    [Theory]
    [InlineData(true, "Arrival")]
    [InlineData(false, "Departure")]
    public void ControllerEventUsesActualGateAndConfirmedDestinyDispatchEnvelope(bool arriving, string eventName)
    {
        const long gate = 50005333;
        DestinyCommand.GateActivity command = new(gate, arriving);
        var encoded = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(command)));
        Assert.Equal("OnClientControllerEvent", BlueMarshal.Text(encoded[0]));
        var arguments = Assert.IsType<object?[]>(encoded[1]);
        Assert.Equal(5, arguments.Length);
        Assert.Equal(gate, Assert.IsType<long>(arguments[0]));
        Assert.Null(arguments[1]);
        Assert.Equal(eventName, BlueMarshal.Text(arguments[2]));
        Assert.Equal(1d, Assert.IsType<double>(arguments[3]));
        Assert.Null(arguments[4]);
        DestinyUpdatesNotification envelope = new([new(42, command)]);
        Assert.Equal("DoDestinyUpdate", envelope.Name);
        var updates = Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(envelope.Arguments[0])));
        var stamped = Assert.IsType<object?[]>(Assert.Single(updates));
        Assert.Equal(42, Assert.IsType<long>(stamped[0]));
        Assert.Equal(BlueMarshal.Encode(command), BlueMarshal.Encode(stamped[1]));
    }

    [Fact]
    public void TwoObserversActivateExactReceivingGateOnceAfterAddBallsAndBeforeShipFlash()
    {
        using Fixture fixture = new();
        var first = fixture.Viewer(1000001);
        var second = fixture.Viewer(1000002);
        fixture.Clock.Seconds = 1;
        fixture.Space.Publish(fixture.Actor(arriving: true));
        foreach (var viewer in new[] { first, second })
        {
            viewer.Refresh();
            var gate = Assert.Single(viewer.Updates.OfType<DestinyCommand.GateActivity>());
            Assert.Equal(new(fixture.Gate.Id, true), gate);
            int add = viewer.Updates.FindIndex(command => command is DestinyCommand.AddBalls);
            int activation = viewer.Updates.IndexOf(gate);
            int flash = viewer.Updates.FindIndex(command => command is DestinyCommand.SpecialEffect { Graphic: "effects.JumpIn" });
            Assert.True(add >= 0 && add < activation && activation < flash);
            viewer.Updates.Clear();
            viewer.Refresh();
            viewer.Snapshot();
            viewer.Refresh();
            Assert.Empty(viewer.Updates.OfType<DestinyCommand.GateActivity>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewlyWitnessedDepartureActivatesGateOnceAcrossAddBallsAndSnapshot(bool snapshot)
    {
        using Fixture fixture = new();
        var viewer = fixture.Viewer(1000001);
        fixture.Clock.Seconds = 1;
        fixture.Space.Publish(fixture.Actor(arriving: false));
        if (snapshot)
        {
            viewer.Snapshot();
        }
        else
        {
            viewer.Refresh();
        }
        var activation = Assert.Single(viewer.Updates.OfType<DestinyCommand.GateActivity>());
        Assert.Equal(new(fixture.Gate.Id, false), activation);
        var ship = Assert.Single(viewer.Updates.OfType<DestinyCommand.JumpOut>());
        Assert.Equal(fixture.Gate.Id, ship.GateId);
        Assert.True(viewer.Updates.IndexOf(activation) < viewer.Updates.IndexOf(ship));
        viewer.Updates.Clear();
        viewer.Refresh();
        viewer.Snapshot();
        viewer.Refresh();
        Assert.Empty(viewer.Updates.OfType<DestinyCommand.GateActivity>());
        Assert.Empty(viewer.Updates.OfType<DestinyCommand.JumpOut>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InitialLoginDuringExistingTransferDoesNotReplayGateActivity(bool arriving)
    {
        using Fixture fixture = new();
        fixture.Clock.Seconds = 1;
        fixture.Space.Publish(fixture.Actor(arriving));
        var late = fixture.Viewer(1000001);
        late.Refresh();
        late.Snapshot();
        Assert.Empty(late.Updates.OfType<DestinyCommand.GateActivity>());
        Assert.Empty(late.Updates.OfType<DestinyCommand.JumpOut>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SavedTransferPublishedAfterItsWindowDoesNotInventHistoricalGateActivity(bool arriving)
    {
        using Fixture fixture = new();
        var viewer = fixture.Viewer(1000001);
        fixture.Clock.Seconds = 1;
        var saved = fixture.Actor(arriving);
        fixture.Clock.Seconds = 30;
        fixture.Space.Publish(saved);
        viewer.Refresh();
        viewer.Snapshot();
        Assert.Empty(viewer.Updates.OfType<DestinyCommand.GateActivity>());
        Assert.Empty(viewer.Updates.OfType<DestinyCommand.JumpOut>());
    }

    [Fact]
    public async Task GateHandoffWaitsForDepartureOutputAndKeepsTheSourceSceneDuringRetries()
    {
        const long accountId = 1000001;
        const long clientId = 42;
        TestClock clock = new();
        using TestWorld world = new(clock: clock);
        var gate = world.Objects.Single(body => body.DestinationGateId is not null);
        var systems = world.Services.GetRequiredService<ISystemObjects>();
        var pilot = world.Store.FindPilot(accountId)!;
        var destination = systems.Load(gate.DestinationSystemId!.Value);
        var exit = destination.Single(body => body.Id == gate.DestinationGateId);
        var resident = world.Store.FindPilot(1000002)!;
        world.Store.SetPosition(resident.ShipId, exit.Position + new SpacePosition(exit.Radius + 1_000, 0, 0), gate.DestinationSystemId);
        world.Store.SetPosition(pilot.ShipId, gate.Position + new SpacePosition(gate.Radius + pilot.Flight.ShipRadius + 100, 0, 0));
        pilot = world.Store.FindPilot(accountId)!;
        SharedSpace space = new();
        using FlightSession observer = new(world.Store, world.Store.FindPilot(1000002)!, clock, space, destination);
        observer.Snapshot();
        observer.Updates.Clear();
        using PilotCallContext context = new(
            accountId,
            pilot.Account,
            [pilot],
            selected => new(world.Store, selected, clock, space, world.Objects),
            clientId,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var flight = context.Flight;
        var sourcePeer = new ShipContact(
            PilotContact.From(pilot) with
            {
                ShipId = 2000000000001,
                Position = pilot.Position! + new SpacePosition(0, 10_000, 0),
            },
            SpacePosition.Zero,
            null,
            0,
            null
        );
        space.Publish(sourcePeer);
        flight.Snapshot();
        flight.Updates.Clear();
        clock.Milliseconds = 999;
        Assert.Same(PilotCallResult.None, flight.StartJump(new FlightCommand.Jump(gate.Id, exit.Id, pilot.ShipId), systems));

        flight.Snapshot();
        Assert.Single(flight.Updates, update => update.Command is DestinyCommand.JumpOut);
        space.Publish(
            sourcePeer with
            {
                Warp = new(sourcePeer.Pilot.Position!, sourcePeer.Pilot.Position! + new SpacePosition(1_000_000, 0, 0), 1, pilot.Flight, 0),
            }
        );
        flight.RefreshPeers();
        Assert.DoesNotContain(flight.Updates, update => update.Command is DestinyCommand.SetState);
        Assert.NotNull(Assert.Single(flight.VisiblePeers).Warp);

        List<BlueInstance> packets = [];
        TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PilotSessionOutput output = new(
            context,
            accountId,
            clientId,
            packet =>
            {
                packets.Add(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(packet))));
                return packets.Count == 1 ? sent.Task : Task.CompletedTask;
            },
            _ => { }
        );
        var sending = output.FlushFlightUpdatesAsync();
        clock.Milliseconds = 5250;
        Assert.False(flight.CompleteJump());
        Assert.Equal(pilot.SystemId, world.Store.FindPilot(accountId)!.SystemId);
        sent.SetResult();
        await sending;

        clock.Milliseconds = 8249;
        Assert.False(flight.CompleteJump());
        Assert.Empty(flight.Updates);
        clock.Milliseconds++;
        using var scope = world.Services.CreateScope();
        PilotSessionTick tick = new(
            clock,
            scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>(),
            scope.ServiceProvider.GetRequiredService<SkillHandler>(),
            scope.ServiceProvider.GetRequiredService<IAsteroidField>()
        );
        await tick.AdvanceAsync(context, output, _ => { }, accountId, clientId);
        Assert.False(flight.CompleteJump());
        Assert.Equal(gate.DestinationSystemId, world.Store.FindPilot(accountId)!.SystemId);
        Assert.EndsWith(".Notification", packets[0].Name);
        var change = Assert.Single(packets, packet => packet.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal));
        Assert.True(packets.IndexOf(change) > 0);
        var body = BlueRead.Tuple(BlueRead.Tuple(change.State, 14)[4], 3);
        var delta = BlueRead.Fields(BlueRead.Tuple(body[1], 2)[1]);
        Assert.Equal(new object?[] { (long)pilot.SystemId, (long)gate.DestinationSystemId.Value }, delta["locationid"]);
        flight.Snapshot();
        Assert.IsType<DestinyCommand.SetState>(flight.Updates[0].Command);
        Assert.Equal(resident.ShipId, Assert.Single(flight.VisiblePeers).Pilot.ShipId);
        Assert.Single(flight.Updates, update => update.Command is DestinyCommand.SpecialEffect { Graphic: "effects.JumpIn" });
        observer.RefreshPeers();
        Assert.Equal(pilot.ShipId, Assert.Single(observer.VisiblePeers).Pilot.ShipId);
        Assert.Single(observer.Updates, update => update.Command is DestinyCommand.AddBalls);
    }

    [Fact]
    public void HumanGateTravelActivatesEachActualGateOnceAndKeepsShipEffects()
    {
        using TestWorld world = new();
        TestClock clock = new();
        var gate = world.Objects.Single(body => body.DestinationGateId is not null);
        var pilot = world.Store.FindPilot(1000001)!;
        world.Store.SetPosition(pilot.ShipId, gate.Position + new SpacePosition(gate.Radius + pilot.Flight.ShipRadius + 100, 0, 0));
        using FlightSession session = new(world.Store, world.Store.FindPilot(1000001)!, clock, new(), world.Objects);
        session.Snapshot();
        session.Updates.Clear();
        Assert.Same(
            PilotCallResult.None,
            session.StartJump(
                new FlightCommand.Jump(gate.Id, gate.DestinationGateId!.Value, pilot.ShipId),
                world.Services.GetRequiredService<ISystemObjects>()
            )
        );
        var departure = Assert.Single(session.Updates, update => update.Command is DestinyCommand.GateActivity);
        Assert.Equal(new DestinyCommand.GateActivity(gate.Id, false), departure.Command);
        Assert.Single(session.Updates, update => update.Command is DestinyCommand.JumpOut);
        session.GateDeparturePublished();
        clock.Seconds = 10;
        Assert.True(session.CompleteJump());
        session.Updates.Clear();
        session.Snapshot();
        var arrival = Assert.Single(session.Updates, update => update.Command is DestinyCommand.GateActivity);
        Assert.Equal(new DestinyCommand.GateActivity(gate.DestinationGateId.Value, true), arrival.Command);
        Assert.Single(session.Updates, update => update.Command is DestinyCommand.SpecialEffect { Graphic: "effects.JumpIn" });
        Assert.True(session.Updates.FindIndex(update => update.Command is DestinyCommand.SetState) < session.Updates.IndexOf(arrival));
        session.Updates.Clear();
        session.Snapshot();
        Assert.DoesNotContain(session.Updates, update => update.Command is DestinyCommand.GateActivity);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestWorld _world = new();
        internal TestClock Clock { get; } = new();
        internal SharedSpace Space { get; } = new();
        internal StaticSpaceObject Gate => _world.Objects.Single(body => body.DestinationGateId is not null);

        internal Viewer Viewer(long account) => new(this, _world.Store.FindPilot(account)!);

        internal ShipContact Actor(bool arriving)
        {
            var pilot = PilotContact.From(_world.Store.FindPilot(1000001)!) with
            {
                PilotId = 90000999,
                ShipId = 91000999,
                Position = Gate.Position + new SpacePosition(Gate.Radius + 1000, 0, 0),
            };
            return new(
                pilot,
                SpacePosition.Zero,
                null,
                0,
                null,
                Arriving: arriving,
                GateArrival: arriving ? new(Gate.Id, Clock.Now) : null,
                DepartureGate: arriving ? null : Gate.Id,
                DepartureAt: arriving ? null : Clock.Now
            );
        }

        public void Dispose() => _world.Dispose();
    }

    private sealed class Viewer
    {
        private readonly Fixture _fixture;
        private readonly FlightScene _scene;
        private readonly FlightSceneContext _context;
        private readonly ShipContact _own;
        internal List<DestinyCommand> Updates { get; } = [];

        internal Viewer(Fixture fixture, DockedPilot pilot)
        {
            _fixture = fixture;
            pilot = pilot with { Position = fixture.Gate.Position + new SpacePosition(fixture.Gate.Radius + 2000, 0, 0) };
            _context = new(pilot, true, true, 1);
            _own = new(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null);
            _scene = new(
                fixture.Space,
                fixture.Clock,
                [fixture.Gate],
                new(Updates.Add),
                Updates.Add,
                update => Updates.Add(update.Command),
                null,
                null,
                null,
                null,
                null
            );
            Snapshot();
            Updates.Clear();
        }

        internal void Snapshot() => _scene.Snapshot(_context, _fixture.Space.Peers(_context.Pilot), _own);

        internal void Refresh() => _scene.RefreshPeers(_context, _own);
    }
}
