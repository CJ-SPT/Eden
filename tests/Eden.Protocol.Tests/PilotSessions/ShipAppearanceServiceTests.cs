using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class ShipAppearanceServiceTests
{
    [Fact]
    public void NativeTimestampCallsRequireSelectionAndOwnedAccessibleShipArguments()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using Client client = new(db, 1000001);
        long ship = db.Store.FindPilot(1000001)!.ShipId;
        Assert.Same(PilotCallResult.Unsupported, client.Call("GetDirtTimestamp", [ship]));
        client.Select();
        long first = Assert.IsType<long>(Reply(client.Call("GetDirtTimestamp", [ship])));
        clock.AdvanceWallTime(TimeSpan.FromDays(7));
        Assert.Equal(first, Reply(client.Call("GetDirtTimestamp", [ship])));
        Assert.Equal(clock.Now, Reply(client.Call("ResetDirtTimestamp", [ship])));
        foreach (var args in new object?[][] { [], [ship, 1L], ["ship"], [0L], [db.Store.FindPilot(1000002)!.ShipId] })
        {
            Assert.IsType<PilotCallResult.Failure>(client.Call("ResetDirtTimestamp", args));
        }
        Assert.IsType<PilotCallResult.Failure>(client.Call("GetDirtTimestamp", [ship], new() { ["pilotID"] = 90000002L }));
    }

    [Fact]
    public void ProductionSpaceSnapshotsAndRepeatedCleaningPublishTheSameDirtTimeToOwnerAndObserver()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        db.Store.SetPosition(1000000000001, new SpacePosition(0, 0, 0));
        db.Store.SetPosition(1000000000002, new SpacePosition(1000, 0, 0));
        using Client owner = new(db, 1000001);
        using Client observer = new(db, 1000002);
        owner.Select();
        observer.Select();
        owner.Context.Flight.Snapshot();
        observer.Context.Flight.Snapshot();
        long ship = owner.Context.Pilot.ShipId;
        long original = Assert.IsType<long>(Reply(owner.Call("GetDirtTimestamp", [ship])));
        var snapshot = Assert.Single(observer.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.SetState>()).Snapshot;
        var seen = Assert.Single(snapshot.Slims.OfType<ShipSlimItem>(), slim => slim.Ship.Pilot.ShipId == ship);
        Assert.Equal(original, seen.Ship.DirtTime);
        Assert.Equal(original, Fields(ReplyModel(seen))["dirtTime"]);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            owner.Context.Flight.Updates.Clear();
            observer.Context.Flight.Updates.Clear();
            clock.AdvanceWallTime(TimeSpan.FromDays(14));
            long cleaned = Assert.IsType<long>(Reply(owner.Call("ResetDirtTimestamp", [ship])));
            observer.Context.Flight.RefreshPeers();
            var ownUpdate = Assert.Single(owner.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.OnSlimItemChange>());
            var peerUpdate = Assert.Single(
                observer.Context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.OnSlimItemChange>()
            );
            Assert.Equal(cleaned, Fields(ReplyModel(ownUpdate.Item))["dirtTime"]);
            Assert.Equal(cleaned, Fields(ReplyModel(peerUpdate.Item))["dirtTime"]);
            Assert.DoesNotContain(observer.Context.Flight.Updates, u => u.Command is DestinyCommand.SetBallPosition);
            observer.Context.Flight.Updates.Clear();
            observer.Context.Flight.RefreshPeers();
            Assert.DoesNotContain(observer.Context.Flight.Updates, u => u.Command is DestinyCommand.OnSlimItemChange);
        }
    }

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return BlueMarshal.Decode(BlueMarshal.Encode(((PilotCallResult.Success)result).Reply));
    }

    private static object? ReplyModel(BlueModel model) => BlueMarshal.Decode(BlueMarshal.Encode(model));

    private static Dictionary<string, object?> Fields(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State);

    private sealed class Client : IDisposable
    {
        private readonly IServiceScope _scope;
        private readonly TestWorld _db;
        private readonly long _account;
        internal PilotCallContext Context { get; }

        internal Client(TestWorld db, long account)
        {
            _db = db;
            _account = account;
            _scope = db.Services.CreateScope();
            Context = new(
                account,
                db.Store.ReadAccount(account),
                db.Store.ListPilots(account),
                _scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
                account,
                SignedSettings.Empty
            );
        }

        internal void Select() => Context.Select(_db.Store.FindPilot(_account)!);

        internal PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, "ship", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
