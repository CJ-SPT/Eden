using System.Diagnostics;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerEncounterTests(ITestOutputHelper output)
{
    private const int Source = 30000141;
    private const int Remote = 30000144;
    private const long Human = 90000001;

    [Fact]
    public async Task SavedPopulationDoesNotExecuteUnobservedJobs()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_ENCOUNTER_SOURCE");
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }
        var directory = TestDirectory.Create("encounter-save-copy-");
        try
        {
            WorldDatabase copy = new(Path.Combine(directory.FullName, "world.sqlite"));
            File.Copy(new WorldDatabase(source).CatalogPath, copy.CatalogPath);
            using (SqliteConnection original = new($"Data Source={source};Mode=ReadOnly;Pooling=False"))
            using (SqliteConnection target = new($"Data Source={copy.Path};Pooling=False"))
            {
                original.Open();
                target.Open();
                original.BackupDatabase(target);
            }
            TestClock clock = new();
            WorldInitialization.Initialize(copy, clock);
            using var host = ServerServices.Build(copy, clock, liveWorld: true);
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            var jobs = scheduler.Jobs().ToArray();
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            worker.Tick();
            using (var metrics = SessionPerformance.Begin("saved inactive population", output.WriteLine, force: true)!)
            {
                for (int i = 0; i < 100; i++)
                {
                    clock.Seconds++;
                    worker.Tick();
                }
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            Assert.Equal(jobs, scheduler.Jobs());
            Assert.Equal(0, host.GetRequiredService<ICapsuleerScene>().MaterializedPilots);
            long started = Stopwatch.GetTimestamp();
            int calls = 0;
            await host.GetRequiredService<ICapsuleerHandoff>().SynchronizeAsync(() => calls++);
            output.WriteLine($"Saved pilots {jobs.Length}, dispatch {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms.");
            Assert.Equal(1, calls);
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            await live.FlushAsync();
            output.WriteLine($"Checkpoint {live.LastCheckpoint}.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EntryCreatesSharedMiningAndTrafficWithoutBackgroundCatchup()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var presence = host.GetRequiredService<IHumanPresence>();
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        Assert.Empty(scheduler.Jobs());
        using var first = presence.Select(Human, Source);
        var jobs = scheduler.Jobs().ToArray();
        Assert.Equal(2, jobs.Length);
        long miner;
        using (WorldDbContext world = new(f.Database.Path))
        {
            miner = world.Set<SavedCapsuleerAgent>().Single(a => a.Career == CapsuleerCareer.Mining).PilotId;
        }
        Assert.Equal(MinerActivity.Mining, scheduler.Miner(miner)!.Work.Activity);
        using (var second = presence.Select(Human + 1, Source))
        {
            Assert.Equal(jobs, scheduler.Jobs());
        }
        first.Dispose();
        f.Clock.Seconds += 3600;
        using var elsewhere = presence.Select(Human, Remote);
        var before = scheduler.Jobs().ToArray();
        var worker = host.GetRequiredService<CapsuleerMaintenance>();
        for (int i = 0; i < 20; i++)
        {
            f.Clock.Seconds++;
            worker.Tick();
        }
        Assert.Equal(before, scheduler.Jobs());
        int calls = 0;
        await host.GetRequiredService<ICapsuleerHandoff>().SynchronizeAsync(Source, () => calls++);
        Assert.Equal(1, calls);
        using var returning = presence.Select(Human + 1, Source);
        Assert.Equal(before.Select(j => j.PilotId), scheduler.Jobs().Select(j => j.PilotId));
        Assert.All(scheduler.Jobs(), j => Assert.True(j.DueTicks > presence.Read().ElapsedTicks));
        Assert.Equal(MinerActivity.Mining, scheduler.Miner(miner)!.Work.Activity);
        await host.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public async Task WitnessedHaulerCrossesTwoGatesWithOneIdentityAndUnchangedCargoAcrossRestart()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        long pilot;
        long ship;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var presence = host.GetRequiredService<IHumanPresence>();
            using var source = presence.Select(Human, Source);
            using var destination = presence.Select(Human + 1, 30000142);
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            using (WorldDbContext world = new(f.Database.Path))
            {
                pilot = world.Set<SavedCapsuleerAgent>().Single(a => a.Career == CapsuleerCareer.Hauling).PilotId;
                ship = world.Pilots.Single(p => p.Id == pilot).ActiveShipId;
            }
            int jumps = 0;
            int previous = Source;
            for (int i = 0; i < 6000 && jumps < 2; i++)
            {
                f.Clock.Seconds++;
                host.GetRequiredService<CapsuleerMaintenance>().Tick();
                var observation = scheduler.Hauler(pilot)!;
                Assert.Null(observation.Work.WaitingReason);
                Assert.Equal(ship, observation.Pilot.ShipId);
                if (observation.Pilot.SystemId != previous)
                {
                    previous = observation.Pilot.SystemId;
                    jumps++;
                }
            }
            Assert.Equal(2, jumps);
            Assert.Equal(Source, previous);
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            using var returning = host.GetRequiredService<IHumanPresence>().Select(Human, Source);
            Assert.Equal(ship, host.GetRequiredService<ICapsuleerScheduler>().Hauler(pilot)!.Pilot.ShipId);
            using WorldDbContext world = new(f.Database.Path);
            Assert.Equal(100, world.Set<Eden.Data.World.Inventory.OwnedItem>().Where(i => i.LocationId == ship).Sum(i => i.Quantity));
            Assert.Empty(world.Set<Eden.Data.World.Market.SavedPersonalOrder>().Where(o => o.PilotId == pilot));
        }
    }

    [Fact]
    public void EmptySystemsHaveNoCareerWorkSqlFittingOrNativeScenes()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var presence = host.GetRequiredService<IHumanPresence>();
        using (var human = presence.Select(Human, Source))
        {
            host.GetRequiredService<CapsuleerMaintenance>().Tick();
        }
        using var elsewhere = presence.Select(Human, Remote);
        var worker = host.GetRequiredService<CapsuleerMaintenance>();
        worker.Tick();
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var before = scheduler.Jobs().ToArray();
        using var metrics = SessionPerformance.Begin("unobserved encounters", output.WriteLine, force: true)!;
        for (int i = 0; i < 20; i++)
        {
            f.Clock.Seconds++;
            worker.Tick();
        }
        Assert.Equal(before, scheduler.Jobs());
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
        Assert.Equal(0, host.GetRequiredService<ICapsuleerScene>().MaterializedPilots);
    }
}
