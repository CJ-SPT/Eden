using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Characters;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class TaskTrackingTestData
{
    internal static void Prepare(TestWorld db)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        foreach (
            var (id, name) in new (long, string)[]
            {
                (95, TaskEvents.Warped),
                (34, TaskEvents.Undocked),
                (90, TaskEvents.Docked),
                (140, TaskEvents.ContactAdded),
                (123, "client_openMap"),
                (138, "client_mouseZoom_in"),
                (124, "client_mouseZoom_out"),
            }
        )
        {
            catalog.Add(
                new SourceRecord
                {
                    Dataset = "client/achievementevents",
                    Key = id.ToString(),
                    Data = JsonSerializer.Serialize(new { name }),
                }
            );
        }
        foreach (
            var (id, client, events) in new (long, bool, long[])[]
            {
                (16, false, [95]),
                (24, false, [34]),
                (10, false, [90]),
                (62, false, [140]),
                (43, true, [123]),
                (60, true, [138, 124]),
            }
        )
        {
            catalog.Add(
                new SourceRecord
                {
                    Dataset = "client/achievements",
                    Key = id.ToString(),
                    Data = JsonSerializer.Serialize(
                        new
                        {
                            isClientAchievement = client,
                            isEnabled = true,
                            simpleConditions = events
                                .Select((statistic, index) => (statistic, index))
                                .ToDictionary(item => item.index.ToString(), item => new { item.statistic, targetValue = 1 }),
                        }
                    ),
                }
            );
        }
        catalog.SaveChanges();
    }

    internal static long Count(TestWorld db, string name, long pilot = 90000001)
    {
        using WorldDbContext world = new(db.Path);
        return world.Set<SavedTaskCounter>().Find(pilot, name)?.Count ?? 0;
    }
}

public sealed class TaskTrackingTests
{
    private const long PilotId = 90000001;

    [Fact]
    public void CumulativeClientSnapshotsArePrivateMonotonicAndPersistEarnedMultiConditionTasks()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        TaskTrackingTestData.Prepare(db);
        var tracking = db.Services.GetRequiredService<IPilotTaskTracking>();
        long time = clock.Now;
        tracking.Update(PilotId, [new(43, time)], [new("client_openMap", 1), new("client_mouseZoom_in", 1)]);
        tracking.Update(PilotId, [new(43, time)], [new("client_openMap", 1), new("client_mouseZoom_in", 0)]);
        Assert.Equal(1, TaskTrackingTestData.Count(db, "client_openMap"));
        Assert.Equal(1, TaskTrackingTestData.Count(db, "client_mouseZoom_in"));
        Assert.Equal(new CompletedTask(43, time), Assert.Single(tracking.Read(PilotId).Completed));
        clock.Seconds++;
        tracking.Update(PilotId, [new(43, time)], [new("client_mouseZoom_out", 1)]);
        var state = tracking.Read(PilotId);
        Assert.Equal(new CompletedTask(60, clock.Now), state.Completed.Single(task => task.Id == 60));
        Assert.False(state.HasWarped);
        Assert.Empty(tracking.Read(90000002).Completed);
        Assert.Empty(tracking.Read(90000002).ClientCounters);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        var after = reopened.GetRequiredService<IPilotTaskTracking>().Read(PilotId);
        Assert.Equal(state.Completed, after.Completed);
        Assert.Equal(state.ClientCounters, after.ClientCounters);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("unknown")]
    [InlineData("server")]
    [InlineData("unearned")]
    [InlineData("future")]
    [InlineData("duplicate")]
    public void InvalidSnapshotsRollBackAllCountersAndCompletions(string invalid)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        TaskTrackingTestData.Prepare(db);
        var tracking = db.Services.GetRequiredService<IPilotTaskTracking>();
        TaskCounter[] counters = [new("client_openMap", 1)];
        CompletedTask[] tasks = [];
        switch (invalid)
        {
            case "negative":
                counters = [.. counters, new("client_mouseZoom_in", -1)];
                break;
            case "unknown":
                counters = [.. counters, new("client_unknown", 1)];
                break;
            case "server":
                tasks = [new(16, clock.Now)];
                break;
            case "unearned":
                tasks = [new(60, clock.Now)];
                break;
            case "future":
                tasks = [new(43, clock.Now + TimeSpan.TicksPerDay)];
                break;
            case "duplicate":
                counters = [.. counters, new("client_openMap", 2)];
                break;
        }
        Assert.Throws<InvalidDataException>(() => tracking.Update(PilotId, tasks, counters));
        Assert.Empty(tracking.Read(PilotId).Completed);
        Assert.Empty(tracking.Read(PilotId).ClientCounters);
        Assert.False(tracking.Read(PilotId).HasWarped);
    }

    [Fact]
    public async Task ConcurrentSnapshotsKeepTheLargestCountAndTheFirstCompletion()
    {
        using TestWorld db = new();
        TaskTrackingTestData.Prepare(db);
        var tracking = db.Services.GetRequiredService<IPilotTaskTracking>();
        await Task.WhenAll(
            new long[] { 1, 9, 3 }.Select(count => Task.Run(() => tracking.Update(PilotId, [], [new("client_openMap", count)])))
        );
        Assert.Equal(9, TaskTrackingTestData.Count(db, "client_openMap"));
        var completion = Assert.Single(tracking.Read(PilotId).Completed);
        tracking.Update(PilotId, [], [new("client_openMap", 2)]);
        Assert.Equal(completion, Assert.Single(tracking.Read(PilotId).Completed));
        Assert.Equal(long.MaxValue, TaskTrackingRules.Increment(long.MaxValue, 1));
    }

    [Fact]
    public void ActualWarpEntryEarnsHistoryOncePerWarpWhileAlignmentAndCancellationDoNot()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        TaskTrackingTestData.Prepare(db);
        var pilot = db.Store.FindPilot(1000001)!;
        db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
        pilot = db.Store.FindPilot(1000001)!;
        var tracking = db.Services.GetRequiredService<IPilotTaskTracking>();
        Assert.False(tracking.Read(PilotId).HasWarped);
        StaticSpaceObject gate = new(1, 16, 10, 2, 1, new(1e12, 0, 0), 1000);
        SharedSpace space = new();
        using FlightSession flight = new(db.Store, pilot, clock, space, [gate, gate with { Id = 2, Position = new(-1e12, 0, 0) }]);
        flight.Snapshot();
        Assert.Null(flight.CallWire("beyonce", "CmdWarpToStuff", ["item", 1L]));
        Assert.False(tracking.Read(PilotId).HasWarped);
        Assert.Null(flight.CallWire("beyonce", "CmdStop", []));
        Assert.False(tracking.Read(PilotId).HasWarped);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            Assert.Null(flight.CallWire("beyonce", "CmdWarpToStuff", ["item", (long)cycle + 1]));
            for (int tick = 0; tick < 20 && !flight.InWarp; tick++)
            {
                clock.Seconds++;
                flight.Checkpoint();
            }
            Assert.True(flight.InWarp);
            Assert.Equal(cycle + 1, TaskTrackingTestData.Count(db, TaskEvents.Warped));
            flight.Checkpoint();
            Assert.Equal(cycle + 1, TaskTrackingTestData.Count(db, TaskEvents.Warped));
            clock.Seconds += 100;
            flight.Checkpoint();
            Assert.False(flight.InWarp);
        }
        var state = tracking.Read(PilotId);
        Assert.True(state.HasWarped);
        Assert.Single(state.Completed, task => task.Id == 16);
        Assert.Empty(state.ClientCounters);
        WorldInitialization.Open(db.Path);
        Assert.True(tracking.Read(PilotId).HasWarped);
    }

    [Fact]
    public void ContactCreationCountsOnceAndVersion43UpgradePreservesExistingContacts()
    {
        using TestWorld db = new();
        TaskTrackingTestData.Prepare(db);
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        contacts.Add(PilotId, 90000002, 5, true);
        contacts.Edit(PilotId, 90000002, 10, false);
        Assert.Throws<InvalidDataException>(() => contacts.Add(PilotId, 90000002, 0, false));
        Assert.Equal(1, TaskTrackingTestData.Count(db, TaskEvents.ContactAdded));
        Assert.Equal(62, Assert.Single(db.Services.GetRequiredService<IPilotTaskTracking>().Read(PilotId).Completed).Id);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldCompletedTask; DROP TABLE WorldTaskCounter; PRAGMA user_version=43");
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        Assert.Single(contacts.Read(PilotId).Addresses);
        Assert.Empty(db.Services.GetRequiredService<IPilotTaskTracking>().Read(PilotId).Completed);
    }

    [Fact]
    public void FailedCompletionWriteRollsBackItsContactAndCounterTogether()
    {
        using TestWorld db = new();
        TaskTrackingTestData.Prepare(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectTask BEFORE INSERT ON WorldCompletedTask
                BEGIN SELECT RAISE(ABORT, 'forced task completion failure'); END;
                """
            );
        }
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        Assert.Throws<DbUpdateException>(() => contacts.Add(PilotId, 90000002, 0, false));
        Assert.Empty(contacts.Read(PilotId).Addresses);
        Assert.Equal(0, TaskTrackingTestData.Count(db, TaskEvents.ContactAdded));
        Assert.Empty(db.Services.GetRequiredService<IPilotTaskTracking>().Read(PilotId).Completed);
    }

    [Fact]
    public void DockingAndUndockingCountOnlyRealLocationTransitions()
    {
        using TestWorld db = new();
        TaskTrackingTestData.Prepare(db);
        var pilot = db.Store.FindPilot(1000001)!;
        db.Store.SetPosition(pilot.ShipId, pilot.Home.Position);
        db.Store.SetPosition(pilot.ShipId, pilot.Home.Position);
        Assert.Equal(1, TaskTrackingTestData.Count(db, TaskEvents.Undocked));
        Assert.Throws<InvalidDataException>(() => db.Store.Dock(PilotId, pilot.ShipId + 1, pilot.StationId));
        Assert.Equal(0, TaskTrackingTestData.Count(db, TaskEvents.Docked));
        db.Store.Dock(PilotId, pilot.ShipId, pilot.StationId);
        Assert.Throws<InvalidDataException>(() => db.Store.Dock(PilotId, pilot.ShipId, pilot.StationId));
        Assert.Equal(1, TaskTrackingTestData.Count(db, TaskEvents.Docked));
        var completed = db.Services.GetRequiredService<IPilotTaskTracking>().Read(PilotId).Completed;
        Assert.Equal(new long[] { 10, 24 }, completed.Select(task => task.Id));
        Assert.False(db.Services.GetRequiredService<IPilotTaskTracking>().Read(PilotId).HasWarped);
    }

    [Theory]
    [InlineData(0, false, true, false)]
    [InlineData(2, false, true, false)]
    [InlineData(3, false, true, true)]
    [InlineData(30, false, false, false)]
    [InlineData(3, true, true, true)]
    public void CatalogConditionsRespectThresholdsAndDisabledDefinitions(long count, bool client, bool enabled, bool earned)
    {
        TaskDefinition definition = new(2, client, enabled, [new("timesMined", 3)]);
        Assert.Equal(earned, TaskTrackingRules.Satisfied(definition, [new("timesMined", count)]));
        Assert.False(TaskTrackingRules.Satisfied(definition with { Conditions = [] }, [new("timesMined", count)]));
    }
}
