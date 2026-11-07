using Eden.Data.Characters;
using Eden.Gameplay.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Characters;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class TaskTrackingServiceTests
{
    [Fact]
    public void NativeSnapshotCallsPreserveNumericTaskKeysAndStringCountersThroughWireAndSelectionGating()
    {
        using Fixture fixture = new();
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("HasEverWarped", []));
        fixture.Context.Select(fixture.Db.Store.FindPilot(1000001)!);
        Assert.Equal(false, Reply(fixture.Call("HasEverWarped", [])));
        Assert.Null(
            Reply(
                fixture.Call(
                    "UpdateClientAchievmentsAndCounters",
                    [
                        new Dictionary<long, object?> { [43] = fixture.Clock.Now },
                        new Dictionary<string, object?> { ["client_openMap"] = 1L },
                    ]
                )
            )
        );
        var state = BlueRead.Fields(Reply(fixture.Call("GetCompletedAchievementsAndClientEventCount", [])));
        var completed = Assert.IsType<Dictionary<long, object?>>(state["completedDict"]);
        Assert.Equal(fixture.Clock.Now, Assert.Single(completed).Value);
        Assert.Equal(43L, Assert.Single(completed).Key);
        Assert.Equal(1L, BlueRead.Fields(state["eventDict"])["client_openMap"]);
        Assert.Empty(fixture.Context.Notifications);
        Assert.Empty(fixture.Db.Services.GetRequiredService<IPilotTaskTracking>().Read(90000002).Completed);
        // A snapshot can echo a server completion fetched at login, but cannot
        // create one or replace its authoritative timestamp.
        fixture.Db.Services.GetRequiredService<IPilotContacts>().Add(90000001, 90000002, 0, false);
        long serverTime = fixture
            .Db.Services.GetRequiredService<IPilotTaskTracking>()
            .Read(90000001)
            .Completed.Single(task => task.Id == 62)
            .Time;
        Assert.Null(
            Reply(
                fixture.Call(
                    "UpdateClientAchievmentsAndCounters",
                    [new Dictionary<long, object?> { [43] = fixture.Clock.Now, [62] = serverTime - 1 }, new Dictionary<string, object?>()]
                )
            )
        );
        Assert.Equal(
            serverTime,
            fixture.Db.Services.GetRequiredService<IPilotTaskTracking>().Read(90000001).Completed.Single(task => task.Id == 62).Time
        );
    }

    [Fact]
    public void MalformedAndForgedRequestsCannotMutateTaskState()
    {
        using Fixture fixture = new();
        fixture.Context.Select(fixture.Db.Store.FindPilot(1000001)!);
        foreach (
            var args in new object?[][]
            {
                [],
                [null, null],
                [new Dictionary<long, object?> { [16] = fixture.Clock.Now }, new Dictionary<string, object?>()],
                [new Dictionary<long, object?>(), new Dictionary<string, object?> { ["timesWarped"] = 1L }],
                [new Dictionary<string, object?> { ["43"] = fixture.Clock.Now }, new Dictionary<string, object?>()],
                [new Dictionary<long, object?>(), new Dictionary<string, object?> { ["client_openMap"] = double.NaN }],
                [new Dictionary<long, object?>(), new Dictionary<string, object?> { ["client_openMap"] = true }],
                [new Dictionary<long, object?>(), Enumerable.Range(0, 1025).ToDictionary(i => "client_" + i, _ => (object?)1L)],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call("UpdateClientAchievmentsAndCounters", args));
        }
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("HasEverWarped", [90000002L]));
        Assert.IsType<PilotCallResult.Failure>(
            fixture.Call("GetCompletedAchievementsAndClientEventCount", [], new() { ["pilotID"] = 90000002L })
        );
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("UnknownTaskRoute", []));
        using WorldDbContext world = new(fixture.Db.Path);
        Assert.Empty(world.Set<SavedCompletedTask>());
        Assert.Empty(world.Set<SavedTaskCounter>());
        Assert.Empty(fixture.Context.Notifications);
    }

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = new();
        public TestWorld Db { get; }
        public PilotCallContext Context { get; }
        private readonly IServiceScope _scope;

        public Fixture()
        {
            Db = new(clock: Clock);
            TaskTrackingTestData.Prepare(Db);
            _scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(1000001, pilot.Account, [pilot], p => new(Db.Store, p, Clock, new(), Db.Objects), 42, SignedSettings.Empty);
        }

        public PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, "monolithTaskTrackerMgr", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
