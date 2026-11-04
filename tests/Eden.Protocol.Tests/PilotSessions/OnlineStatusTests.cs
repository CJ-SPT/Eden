using Eden.Gameplay.Characters;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class OnlineStatusTests
{
    [Fact]
    public void WatchlistPresenceTracksSelectionLogoffAndDisconnectWithoutDockingFlicker()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        db.Services.GetRequiredService<IPilotContacts>().Add(90000001, 90000002, 5, true);
        using Client first = new(db, 1000001, clock);
        Assert.False(first.Status(90000002));
        var initial = Assert.IsType<CompatibilityRowset>(first.Call("GetInitialState", []).Reply);
        var row = Assert.IsType<OnlineContactReply>(Assert.Single(initial.Rows));
        Assert.Equal(new OnlineContactReply(90000002, false), row);
        using Client second = new(db, 1000002, clock);
        Tick();
        AssertEvent(true);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            Assert.Null(second.Context.Flight.CallWire("ship", "Undock", [second.Context.Pilot.ShipId]));
            second.Guests.Advance(second.Context);
            Tick();
            Assert.True(first.Status(90000002));
            Assert.Empty(first.Context.Notifications);
            Assert.Null(
                second.Context.Flight.CallWire("beyonce", "CmdDock", [(long)second.Context.Pilot.StationId, second.Context.Pilot.ShipId])
            );
            second.Guests.Advance(second.Context);
            Tick();
            Assert.Empty(first.Context.Notifications);
            second.Guests.Dispose();
            Tick();
            AssertEvent(false);
            second.Guests.Advance(second.Context);
            Tick();
            AssertEvent(true);
            Tick();
            Assert.Empty(first.Context.Notifications);
        }
        var result = second.Router.Call(second.Context, "userSvc", "UserLogOffCharacter", Array.Empty<object?>(), _ => { });
        Assert.IsType<PilotCallResult.Success>(result);
        Tick();
        AssertEvent(false);
        Assert.False(first.Status(90000002));
        Assert.False(first.Status(123));
        Assert.IsType<PilotCallResult.UnsupportedCall>(
            second.Router.Call(second.Context, "onlineStatus", "GetInitialState", Array.Empty<object?>(), _ => { })
        );

        void Tick()
        {
            clock.Seconds++;
            first.Online.Advance(first.Context);
        }

        void AssertEvent(bool online)
        {
            var notification = Assert.IsType<ContactPresenceChanged>(Assert.Single(first.Context.Notifications));
            Assert.Equal(online ? "OnContactLoggedOn" : "OnContactLoggedOff", notification.Name);
            Assert.Equal(new object?[] { 90000002L }, notification.Arguments);
            first.Context.Notifications.Clear();
        }
    }

    [Fact]
    public void BlocksHidePresenceAndMalformedRequestsCannotReadAnotherPilotsWatchlist()
    {
        using TestWorld db = new();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        contacts.Add(90000001, 90000002, 0, true);
        using Client first = new(db, 1000001, TimeProvider.System);
        using Client second = new(db, 1000002, TimeProvider.System);
        first.Call("GetInitialState", []);
        Assert.True(first.Status(90000002));
        contacts.SetBlocked(90000001, [90000002], true);
        first.Online.Advance(first.Context);
        Assert.False(first.Status(90000002));
        Assert.Equal("OnContactLoggedOff", Assert.Single(first.Context.Notifications).Name);
        first.Context.Notifications.Clear();
        contacts.SetBlocked(90000001, [90000002], false);
        first.Online.Advance(first.Context);
        Assert.Equal("OnContactLoggedOn", Assert.Single(first.Context.Notifications).Name);
        Assert.IsType<PilotCallResult.Failure>(
            first.Router.Call(first.Context, "onlineStatus", "GetInitialState", new object?[] { 90000002L }, _ => { })
        );
        Assert.IsType<PilotCallResult.Failure>(
            first.Router.Call(first.Context, "onlineStatus", "GetOnlineStatus", new object?[] { "bad" }, _ => { })
        );
        Assert.Empty(Assert.IsType<CompatibilityRowset>(second.Call("GetInitialState", []).Reply).Rows);
        first.Online.Reset();
        first.Context.Notifications.Clear();
        first.Online.Advance(first.Context);
        Assert.Empty(first.Context.Notifications);
    }

    private sealed class Client : IDisposable
    {
        private readonly IServiceScope _scope;
        public PilotCallContext Context { get; }
        public PilotCallRouter Router => _scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        public OnlineStatusHandler Online => _scope.ServiceProvider.GetRequiredService<OnlineStatusHandler>();
        public StationGuestHandler Guests => _scope.ServiceProvider.GetRequiredService<StationGuestHandler>();

        public Client(TestWorld db, long account, TimeProvider clock)
        {
            _scope = db.Services.CreateScope();
            var pilot = db.Store.FindPilot(account)!;
            Context = new(account, pilot.Account, [pilot], p => new(db.Store, p, clock, new(), db.Objects), account, SignedSettings.Empty);
            Context.Select(pilot);
            _scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Enter(Context);
        }

        public PilotCallResult.Success Call(string method, object?[] args) =>
            Assert.IsType<PilotCallResult.Success>(Router.Call(Context, "onlineStatus", method, args, _ => { }));

        public bool Status(long pilotId) =>
            Assert.IsType<bool>(BlueMarshal.Decode(BlueMarshal.Encode(Call("GetOnlineStatus", [pilotId]).Reply)));

        public void Dispose()
        {
            Guests.Dispose();
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
