using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Docking;

public sealed class StationGuestTests
{
    [Fact]
    public void SnapshotIncludesOnlyConnectedPilotsInTheSameStationAndResolvesTheirNames()
    {
        using TestWorld world = new();
        using Client first = new(world, world.Store.FindPilot(1000001)!);
        var wing = world.Store.FindPilot(1000002)!;
        Assert.Equal(new[] { first.Context.Pilot.PilotId }, first.Snapshot().Select(guest => guest.CharacterId));
        using Client second = new(world, wing with { Home = wing.Home with { Id = wing.StationId + 1 } }, 1000002);
        second.Guests.Advance(second.Context);
        first.Guests.Advance(first.Context);
        Assert.Empty(first.Context.Notifications);
        Assert.Single(first.Snapshot());
        second.Context.ChangeShip(wing);
        second.Guests.Advance(second.Context);
        var snapshot = first.Snapshot();
        Assert.Equal(new[] { first.Context.Pilot.PilotId, wing.PilotId }, snapshot.Select(guest => guest.CharacterId));
        Assert.Equal(wing.CorpId, snapshot[1].CorporationId);
        Assert.Equal(
            BlueMarshal.Encode(
                new List<object?>
                {
                    new object?[] { first.Context.Pilot.PilotId, first.Context.Pilot.CorpId, null, null },
                    new object?[] { wing.PilotId, wing.CorpId, null, null },
                }
            ),
            BlueMarshal.Encode(new BlueList<StationGuest>(snapshot))
        );
        var owners = Assert.IsType<OwnerTable>(first.Call("config", "GetMultiOwnersEx", [new List<object?> { wing.PilotId }]).Reply);
        Assert.Equal(new OwnerRow(wing.PilotId, wing.Name, wing.Profile.GenderId), Assert.Single(owners.Owners));
        var tickers = Assert.IsType<CorporationTickerTable>(
            first.Call("config", "GetMultiCorpTickerNamesEx", [new List<object?> { (long)wing.CorpId }]).Reply
        );
        Assert.Equal(new CorporationTickerRow(wing.CorpId, wing.CorporationTicker), Assert.Single(tickers.Corporations));
        Assert.Null(second.Context.Flight.CallWire("ship", "Undock", [wing.ShipId]));
        second.Guests.Advance(second.Context);
        Assert.Single(first.Snapshot());
        Assert.Empty(second.Snapshot());
    }

    [Fact]
    public async Task IdleDockedTicksSendSingleArrivalsAndDeparturesAcrossRepeatedDockingCycles()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock);
        using Client first = new(world, world.Store.FindPilot(1000001)!);
        first.Updates.Enter(first.Context);
        first.Snapshot();
        Assert.Null(first.Context.NextSkillCompletion);
        Assert.False(first.Context.Flight.InBallpark);
        Assert.False(first.Context.Flight.Moving);
        Assert.True(first.Updates.IsMonitoring);
        using Client second = new(world, world.Store.FindPilot(1000002)!, 1000002);
        second.Updates.Enter(second.Context);
        List<BlueInstance> packets = [];
        PilotSessionOutput output = new(
            first.Context,
            first.Context.AccountId,
            42,
            packet =>
            {
                packets.Add(Assert.IsType<BlueInstance>(packet));
                return Task.CompletedTask;
            },
            _ => { }
        );
        PilotSessionTick tick = new(
            clock,
            first.Updates,
            first.Services.GetRequiredService<SkillHandler>(),
            first.Services.GetRequiredService<IAsteroidField>()
        );
        await Tick();
        AssertEvent("OnCharNowInStation");
        for (int cycle = 0; cycle < 3; cycle++)
        {
            Assert.Null(second.Context.Flight.CallWire("ship", "Undock", [second.Context.Pilot.ShipId]));
            second.Updates.Advance(second.Context);
            await Tick();
            AssertEvent("OnCharNoLongerInStation");
            Assert.Null(
                second.Context.Flight.CallWire("beyonce", "CmdDock", [(long)second.Context.Pilot.StationId, second.Context.Pilot.ShipId])
            );
            second.Updates.Advance(second.Context);
            await Tick();
            AssertEvent("OnCharNowInStation");
            await Tick();
            Assert.Empty(packets);
        }

        async Task Tick()
        {
            clock.Seconds++;
            await tick.AdvanceAsync(first.Context, output, _ => { }, first.Context.AccountId, 42);
        }

        void AssertEvent(string name)
        {
            var packet = Assert.Single(packets);
            var state = BlueRead.Tuple(packet.State, 14);
            var destination = Assert.IsType<BlueInstance>(state[2]);
            Assert.Equal(name, BlueRead.Tuple(destination.State, 4)[1]);
            var payload = BlueRead.Tuple(BlueRead.Tuple(state[4], 1)[0], 2);
            var service = BlueRead.Tuple(BlueMarshal.Decode(BlueRead.Bytes(payload[1])), 2);
            var arguments = BlueRead.Tuple(BlueRead.Tuple(service[1], 2)[1], 1);
            Assert.Equal(
                new object?[] { second.Context.Pilot.PilotId, (long)second.Context.Pilot.CorpId, null, null },
                BlueRead.Tuple(arguments[0], 4)
            );
            Assert.Empty(first.Context.Notifications);
            packets.Clear();
        }
    }

    [Fact]
    public void LogoffDisconnectAndReconnectRemoveStaleGuestsWithoutShipSwapFlicker()
    {
        using TestWorld world = new();
        using Client first = new(world, world.Store.FindPilot(1000001)!);
        first.Updates.Enter(first.Context);
        using Client second = new(world, world.Store.FindPilot(1000002)!, 1000002);
        second.Updates.Enter(second.Context);
        Assert.Equal(2, first.Snapshot().Count);
        var wing = second.Context.Pilot;
        second.Context.ChangeShip(wing with { Ship = wing.Ship with { Name = "Renamed guest ship" } });
        second.Updates.Advance(second.Context);
        first.Guests.Advance(first.Context);
        Assert.Empty(first.Context.Notifications);
        second.Call("userSvc", "UserLogOffCharacter", []);
        first.Guests.Advance(first.Context);
        Assert.IsType<StationGuestDeparted>(Assert.Single(first.Context.Notifications));
        first.Context.Notifications.Clear();
        Assert.Single(first.Snapshot());
        second.Context.Select(wing);
        second.Updates.Enter(second.Context);
        first.Guests.Advance(first.Context);
        Assert.IsType<StationGuestArrived>(Assert.Single(first.Context.Notifications));
        first.Context.Notifications.Clear();
        second.Dispose();
        first.Guests.Advance(first.Context);
        Assert.IsType<StationGuestDeparted>(Assert.Single(first.Context.Notifications));
        Assert.Single(first.Snapshot());
        using Client reconnected = new(world, wing, 1000002);
        reconnected.Updates.Enter(reconnected.Context);
        Assert.Equal(2, first.Snapshot().Count);
        Assert.Equal(2, reconnected.Snapshot().Count);
    }

    [Fact]
    public void SnapshotRefreshPreventsDuplicateEventsAndInvalidRequestsPreservePresence()
    {
        using TestWorld world = new();
        using Client first = new(world, world.Store.FindPilot(1000001)!);
        first.Updates.Enter(first.Context);
        first.Snapshot();
        using Client second = new(world, world.Store.FindPilot(1000002)!, 1000002);
        second.Updates.Enter(second.Context);
        Assert.Equal(2, first.Snapshot().Count);
        first.Guests.Advance(first.Context);
        Assert.Empty(first.Context.Notifications);
        var router = first.Services.GetRequiredService<PilotCallRouter>();
        Assert.IsType<PilotCallResult.Failure>(router.Call(first.Context, "station", "GetGuests", new object?[] { 1L }, _ => { }));
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(
                first.Context,
                "station",
                "GetGuests",
                Array.Empty<object?>(),
                _ => { },
                new() { ["stationID"] = 1L, ["machoVersion"] = 1L }
            )
        );
        Assert.Equal(2, first.Snapshot().Count);
        Assert.Equal(2, second.Snapshot().Count);
        first.Context.LogOff();
        first.Guests.Advance(first.Context);
        Assert.IsType<PilotCallResult.UnsupportedCall>(
            router.Call(first.Context, "station", "GetGuests", Array.Empty<object?>(), _ => { })
        );
        Assert.Single(second.Snapshot());
    }

    private sealed class Client : IDisposable
    {
        private readonly IServiceScope _scope;
        public IServiceProvider Services => _scope.ServiceProvider;
        public PilotCallContext Context { get; }
        public StationGuestHandler Guests => Services.GetRequiredService<StationGuestHandler>();
        public PilotUpdateCoordinator Updates => Services.GetRequiredService<PilotUpdateCoordinator>();

        public Client(TestWorld world, DockedPilot pilot, long accountId = 1000001)
        {
            _scope = world.Services.CreateScope();
            Context = new(
                accountId,
                pilot.Account,
                [pilot],
                Services.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            );
            Context.Select(pilot);
        }

        public PilotCallResult.Success Call(string service, string method, object?[] args) =>
            Assert.IsType<PilotCallResult.Success>(
                Services.GetRequiredService<PilotCallRouter>().Call(Context, service, method, args, _ => { })
            );

        public IReadOnlyList<StationGuest> Snapshot() =>
            Assert.IsType<BlueList<StationGuest>>(Call("station", "GetGuests", []).Reply).Items;

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
