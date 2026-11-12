using System.Diagnostics;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Docking;
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

    [Theory]
    [InlineData(0, MinerEncounterOpening.Returning)]
    [InlineData(1, MinerEncounterOpening.Mining)]
    [InlineData(3, MinerEncounterOpening.Docked)]
    public void SeededOpeningAndTrafficProfilesRetainTheSameChoices(long seed, MinerEncounterOpening expected)
    {
        Assert.Equal(expected, CapsuleerEncounterRules.Opening(seed, Source));
        Assert.Equal(0, CapsuleerEncounterRules.Haulers(2, Source, 1, 1));
        Assert.Equal(1, CapsuleerEncounterRules.Haulers(1, Source, 1, 1));
        Assert.InRange(CapsuleerEncounterRules.Haulers(seed, Source, 3, 1), 2, 3);
        Assert.InRange(CapsuleerEncounterRules.Haulers(seed, Source, 4, 1), 3, 4);
        Assert.InRange(CapsuleerEncounterRules.Haulers(seed, Source, 7, 18), 5, 6);
    }

    [Fact]
    public void EmptyTrafficProfileStillPublishesItsPreparedEligibility()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var population = host.GetRequiredService<ICapsuleerPopulation>();
        Assert.Equal(0, population.Summary().RegisteredSystems);
        using var selected = host.GetRequiredService<IHumanPresence>().Select(Human, 30000142);
        Assert.Equal(0, population.Summary().RegisteredSystems);
        host.GetRequiredService<ICapsuleerBackgroundPreparation>().PrepareNext();
        Assert.Equal(1, population.Summary().RegisteredSystems);
        Assert.Equal(1, population.Summary().EligibleSystems);
        Assert.Equal(0, population.Summary().Agents);
    }

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
            using (WorldDbContext world = new(copy.Path))
            {
                var pilot = world
                    .Pilots.Include(p => p.ActiveShip)
                        .ThenInclude(s => s.Position)
                    .Where(p => p.AccountId > 0)
                    .OrderBy(p => p.Id)
                    .First();
                pilot.ActiveShip.StationId = 60015157;
                if (pilot.ActiveShip.Position is { } position)
                {
                    world.Positions.Remove(position);
                }
                world.SaveChanges();
            }
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
            await MeasureDockedFrames(host, copy, clock);
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
    public async Task SmallWorldRunsTheSameDockedFrameWorkload()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Source);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        live.MeasureCheckpoints = true;
        await MeasureDockedFrames(host, f.Database, f.Clock);
        await live.FlushAsync();
        output.WriteLine($"Small checkpoint {live.LastCheckpoint}.");
    }

    private async Task MeasureDockedFrames(ServiceProvider host, WorldDatabase database, TestClock clock)
    {
        using var scope = host.CreateScope();
        var services = scope.ServiceProvider;
        long account;
        using (WorldDbContext world = new(database.Path))
        {
            account = world.Pilots.Where(p => p.AccountId > 0).OrderBy(p => p.Id).Select(p => p.AccountId).First();
        }
        var lookup = services.GetRequiredService<IPilotLookup>();
        using PilotCallContext context = new(
            account,
            lookup.ReadAccount(account),
            lookup.ListPilots(account),
            services.GetRequiredService<FlightSessionFactory>().Create,
            account,
            SignedSettings.Empty
        );
        long started = Stopwatch.GetTimestamp();
        context.Select(services.GetRequiredService<SqliteWorld>().FindPilot(account)!);
        var updates = services.GetRequiredService<PilotUpdateCoordinator>();
        updates.Enter(context);
        output.WriteLine($"Docked entry {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms.");
        PilotSessionOutput sink = new(
            context,
            account,
            account,
            _ =>
            {
                Assert.False(services.GetRequiredService<IWorldSynchronization>().IsEntered);
                return Task.CompletedTask;
            },
            _ => { }
        );
        PilotSessionTick tick = new(
            clock,
            updates,
            services.GetRequiredService<SkillHandler>(),
            services.GetRequiredService<IAsteroidField>(),
            services.GetRequiredService<IPilotOperationScope>(),
            anomalies: services.GetRequiredService<CombatAnomalyHandler>(),
            handoff: services.GetRequiredService<ICapsuleerHandoff>(),
            presentation: services.GetRequiredService<ICapsuleerPresentation>()
        );
        for (int i = 0; i < 3; i++)
        {
            await tick.AdvanceAsync(context, sink, _ => { }, account, account);
        }
        using var metrics = SessionPerformance.Begin("ten warmed docked frames", output.WriteLine, force: true)!;
        for (int i = 0; i < 10; i++)
        {
            await tick.AdvanceAsync(context, sink, _ => { }, account, account);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public async Task EntrySharesMiningAndTrafficAndBothPauseOffscreen()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Source);
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
        for (int i = 0; i < 70; i++)
        {
            f.Clock.Seconds++;
            worker.Tick();
        }
        Assert.Equal(before, scheduler.Jobs());
        int calls = 0;
        await host.GetRequiredService<ICapsuleerHandoff>().SynchronizeAsync(Source, () => calls++);
        Assert.Equal(1, calls);
        var travelerBeforeEntry = scheduler.Jobs().Single(j => j.PilotId != miner);
        using var returning = presence.Select(Human + 1, Source);
        Assert.Equal(before.Select(j => j.PilotId).Order(), scheduler.Jobs().Select(j => j.PilotId).Order());
        long elapsed = presence.Read().ElapsedTicks;
        var paidBeforeSampling = scheduler.Miner(miner)!.Work;
        var travelerAfterEntry = scheduler.Jobs().Single(j => j.PilotId != miner);
        // Entry may settle one bounded continuation, never a history of remote actions.
        Assert.InRange(travelerAfterEntry.Sequence - travelerBeforeEntry.Sequence, 0, 1);
        Assert.True(travelerAfterEntry.Revision >= travelerBeforeEntry.Revision);
        Assert.Equal(paidBeforeSampling.Activity, scheduler.Miner(miner)!.Work.Activity);
        var pending = scheduler.Jobs().ToArray();
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        Assert.Equal(pending, scheduler.Jobs());
        Assert.Equal(paidBeforeSampling.Cycles, scheduler.Miner(miner)!.Work.Cycles);
        worker.Tick();
        Assert.All(scheduler.Jobs(), j => Assert.True(j.DueTicks > elapsed));
        var settled = scheduler.Jobs().ToArray();
        worker.Tick();
        Assert.Equal(settled, scheduler.Jobs());
        await host.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public void ScheduledHaulerCanBeMetOnItsRealRouteWithTheSameShipAndCargo()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Source);
        using (WorldDbContext world = new(f.Database.Path))
        {
            var route = world.Set<SavedCapsuleerHauler>().Single();
            var work = HaulerCheckpoint.Read(route.Progress)!;
            new HaulerCheckpoint(
                route.PilotId,
                work with
                {
                    Journey = new(TrafficJourneyKind.Regional, 60015157, 60015157, 60003760, [Source, 30000142], 1),
                }
            ).Apply(world);
            world.SaveChanges();
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var presence = host.GetRequiredService<IHumanPresence>();
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        long pilot;
        long ship;
        using var first = presence.Select(Human, Source);
        using (WorldDbContext world = new(f.Database.Path))
        {
            pilot = world.Set<SavedCapsuleerAgent>().Single(a => a.Career == CapsuleerCareer.Hauling).PilotId;
            ship = scheduler.Hauler(pilot)!.Pilot.ShipId;
        }
        using var elsewhere = presence.Select(Human + 1, Remote);
        var worker = host.GetRequiredService<CapsuleerMaintenance>();
        for (int second = 0; second < 1200 && scheduler.Hauler(pilot)!.Pilot.SystemId != 30000142; second++)
        {
            f.Clock.Seconds++;
            worker.Tick();
        }
        elsewhere.Transfer(30000142);
        var encountered = scheduler.Hauler(pilot)!;
        Assert.Equal(ship, encountered.Pilot.ShipId);
        Assert.Equal(30000142, encountered.Pilot.SystemId);
        Assert.Equal(HaulerActivity.Flying, encountered.Work.Activity);
        Assert.NotNull(encountered.Work.Navigation.WarpDestination);
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Equal(100, saved.Set<Eden.Data.World.Inventory.OwnedItem>().Where(i => i.LocationId == ship).Sum(i => i.Quantity));
        Assert.Single(saved.Ships, s => s.Id == ship);
    }

    [Fact]
    public void PendingGateTransferWaitsForItsSourceAndPausesInAnEmptyDestination()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Source);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var presence = host.GetRequiredService<IHumanPresence>();
        using var human = presence.Select(Human, Source);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        long pilot;
        using (WorldDbContext world = new(f.Database.Path))
        {
            pilot = world.Set<SavedCapsuleerAgent>().Single(a => a.Career == CapsuleerCareer.Hauling).PilotId;
        }
        for (int i = 0; i < 3000 && scheduler.Hauler(pilot)!.Work.Activity != HaulerActivity.Jumping; i++)
        {
            f.Clock.Seconds++;
            host.GetRequiredService<CapsuleerMaintenance>().Tick();
        }
        Assert.Equal(HaulerActivity.Jumping, scheduler.Hauler(pilot)!.Work.Activity);
        var pending = scheduler.Jobs().Single(job => job.PilotId == pilot);
        human.Transfer(Remote);
        f.Clock.Seconds += 3600;
        Assert.Equal(0, scheduler.Run(0).Turn.Completed);
        Assert.Contains(pending, scheduler.Jobs());
        Assert.Equal(Source, scheduler.Hauler(pilot)!.Pilot.SystemId);

        human.Transfer(Source);
        scheduler.Run(0);
        for (int turn = 0; turn < 30 && scheduler.Hauler(pilot)!.Pilot.SystemId == Source; turn++)
        {
            f.Clock.Seconds++;
            scheduler.Run(turn);
        }
        var arrived = scheduler.Hauler(pilot)!;
        Assert.Equal(30000142, arrived.Pilot.SystemId);
        Assert.NotNull(arrived.Work.Journey);
        var paused = scheduler.Jobs().Single(job => job.PilotId == pilot);
        f.Clock.Seconds += 3600;
        scheduler.Run(0);
        Assert.Contains(paused, scheduler.Jobs());
        human.Dispose();
        var elapsed = presence.Read().ElapsedTicks;
        f.Clock.Seconds += 3600;
        Assert.Equal(elapsed, presence.Read().ElapsedTicks);
        var ship = scheduler.Hauler(pilot)!.Pilot.ShipId;
        scheduler.Run(host.GetRequiredService<ICapsuleerScene>().Stamp);
        Assert.Equal(ship, scheduler.Hauler(pilot)!.Pilot.ShipId);
    }

    [Fact]
    public async Task WitnessedHaulerCrossesTwoGatesWithOneIdentityAndUnchangedCargoAcrossRestart()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Source);
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
    public void EmptySystemFramesHaveNoSqlFittingOrNativeScenesWhileTravelIsPaused()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Source);
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
        f.Clock.Seconds += 70;
        for (int turn = 0; turn < 10; turn++)
        {
            worker.Tick();
            f.Clock.Milliseconds += 50;
        }
        Assert.Equal(before, scheduler.Jobs());
        using var metrics = SessionPerformance.Begin("unobserved encounters", output.WriteLine, force: true)!;
        for (int i = 0; i < 20; i++)
        {
            worker.Tick();
        }
        Assert.Equal(before, scheduler.Jobs());
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
        Assert.Equal(0, host.GetRequiredService<ICapsuleerScene>().MaterializedPilots);
    }
}
