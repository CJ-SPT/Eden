using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerRuntimeTests
{
    private const int SourceSystem = 30000141;
    private const int SourceStation = 60015157;
    private const int OreType = 1230;

    [Fact]
    public void UnchangedSchedulerReadsDoNotRebuildMembershipBetweenJobs()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var selected = host.GetRequiredService<IHumanPresence>().Select(90000001, SourceSystem);
        scheduler.Run(0);
        scheduler.Capture();
        long reconciliations = scheduler.Reconciliations;
        using (var metrics = SessionPerformance.Begin("unchanged scheduler membership", _ => { }, force: true)!)
        {
            for (int repeat = 0; repeat < 20; repeat++)
            {
                Assert.Equal(0, scheduler.Run(0, maximumActions: 1).Turn.Completed);
                scheduler.Capture();
            }
            Assert.Equal(reconciliations, scheduler.Reconciliations);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        selected.Transfer(30000144);
        scheduler.Capture();
        Assert.True(scheduler.Reconciliations > reconciliations);
    }

    [Fact]
    public async Task ConcurrentWakesExecuteEachDueRoleOnceAndCheckpointTheSameJobGeneration()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        CapsuleerJob[] committed;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            using var selected = host.GetRequiredService<IHumanPresence>().Select(90000001, SourceSystem);
            Assert.Equal(2, scheduler.Jobs().Count);
            f.Clock.Seconds++;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => scheduler.Run(1))));
            committed = scheduler.Jobs().OrderBy(j => j.PilotId).ToArray();
            Assert.All(committed, job => Assert.Equal(1L, job.Sequence));
            Assert.Equal(0, scheduler.Run(1).Turn.Completed);
            var live = host.GetRequiredService<LiveWorld>();
            Assert.Equal(1, live.MinerCheckpoint!.Work.Stamp);
            Assert.Equal(1, live.HaulerCheckpoint!.Work.Stamp);
            await live.FlushAsync();
        }
        f.Clock.Seconds += 3600;
        WorldInitialization.Initialize(f.Database, f.Clock);
        using var reopened = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<ICapsuleerScheduler>();
        Assert.Empty(restored.Jobs());
        using (WorldDbContext saved = new(f.Database.Path))
        {
            Assert.Equal(committed, CapsuleerPopulationStore.Jobs(saved).OrderBy(job => job.PilotId));
        }
        Assert.Equal(0, restored.Run(100).Turn.Completed);
        using var human = reopened.GetRequiredService<IHumanPresence>().Select(90000001, SourceSystem);
        Assert.Equal(committed, restored.Jobs().OrderBy(job => job.PilotId));
        restored.Run(100);
        Assert.Equal(0, reopened.GetRequiredService<ICapsuleerDiagnostics>().Read().CompletedActions);
        long elapsed = reopened.GetRequiredService<IHumanPresence>().Read().ElapsedTicks;
        Assert.All(restored.Jobs(), job => Assert.True(job.DueTicks > elapsed));
    }

    [Fact]
    public void FailedUndockRollsBackFittingAndResidentProgress()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        long miner = host.GetRequiredService<ICapsuleerCommunity>().Pilots().Single(p => p.Career == CapsuleerCareer.Mining).PilotId;
        using var selected = host.GetRequiredService<IHumanPresence>().Select(90000001, SourceSystem);
        var expected = scheduler.Jobs().Single(j => j.PilotId == miner);
        var before = scheduler.Miner(miner)!;
        using (CapsuleerOperation operation = new(f.Database, expected, host.GetRequiredService<Eden.World.Market.MarketChangeSignal>()))
        {
            var world = operation.World;
            var pilot = world.Pilots.Single(p => p.Id == miner);
            var laser = world.Set<OwnedItem>().Single(i => i.OwnerId == miner);
            PilotFittingStore.Fit(world, operation.Catalog, miner, [laser.Id], SourceStation, (int)InventoryFlag.AutoFit, f.Clock.Now);
            PilotFittingStore.SetOnline(world, operation.Catalog, miner, pilot.ActiveShipId, laser.Id, true, f.Clock.Now);
            var fitted = host.GetRequiredService<SqliteWorld>().FindPilot(world, pilot.AccountId, miner)!;
            var launch = StationUndocking.Launch(fitted.ShipId, fitted.Home, fitted.Flight);
            SqliteWorld.SetPosition(world, operation.Catalog, fitted.ShipId, launch.Position, SourceSystem, f.Clock.Now);
            world.Set<PilotWallet>().Find(miner)!.Cents = -1;
            Assert.Throws<InvalidDataException>(() =>
                operation.Commit(expected.Next(1, expected.Action), new MinerCheckpoint(miner, before.Work with { Trips = 1 }), null)
            );
        }
        f.Clock.Seconds++;
        Assert.Equal(expected, scheduler.Jobs().Single(j => j.PilotId == miner));
        before = scheduler.Miner(miner)!;
        Assert.Null(before.Pilot.Position);
        Assert.Equal(0, before.Work.Stamp);
        Assert.Equal(0, before.Work.Trips);
        using (WorldDbContext world = new(f.Database.Path))
        {
            var laser = world.Set<OwnedItem>().Single(i => i.OwnerId == miner);
            Assert.Equal((long)SourceStation, laser.LocationId);
            Assert.Equal((int)InventoryFlag.Hangar, laser.FlagId);
            Assert.False(laser.Singleton);
            Assert.False(laser.Online);
            Assert.Empty(world.Positions.Where(p => p.ShipId == before.Pilot.ShipId));
        }
        scheduler.Run(1);
        var committed = scheduler.Miner(miner)!;
        Assert.Equal(MinerActivity.FlyingToBelt, committed.Work.Activity);
        Assert.NotNull(committed.Pilot.Position);
        Assert.Equal(1, committed.Work.Trips);
        Assert.Equal(1L, scheduler.Jobs().Single(j => j.PilotId == miner).Sequence);
        Assert.Equal(0, scheduler.Run(1).Turn.Completed);
    }

    [Fact]
    public void Schema116PaidCycleMigrationRetainsRemainingDeadlineAndResumeRebasesItsNativeCooldown()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        MinerProgress paid;
        long miner;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            MinerObservation observation;
            do
            {
                observation = CapsuleerTestDriver.Miner(host, f.Clock);
            } while (observation.Work.Activity != MinerActivity.Mining);
            paid = observation.Work;
            miner = observation.Pilot.PilotId;
        }
        const long elapsed = 123456789;
        using (WorldDbContext world = new(f.Database.Path))
        {
            new MinerCheckpoint(miner, paid with { ActiveTicks = 0, DueTicks = 0, ClockOffsetTicks = 0 }).Apply(world);
            world.Set<SavedCapsuleerJob>().ExecuteDelete();
            world.Database.ExecuteSql($"UPDATE WorldCapsuleerClock SET ElapsedTicks={elapsed} WHERE Id=1");
            world.Database.ExecuteSqlRaw("PRAGMA user_version=116");
        }
        f.Clock.Seconds += 3600;
        WorldInitialization.Initialize(f.Database, f.Clock);
        long deadline = CapsuleerScheduleRules.MigrateDeadline(elapsed, paid.Stamp, paid.DueStamp);
        using (WorldDbContext world = new(f.Database.Path))
        {
            var migrated = MinerCheckpoint.Read(world.Set<SavedCapsuleerCommunity>().Single().Progress)!;
            Assert.Equal(paid.Navigation, migrated.Navigation);
            Assert.Equal(paid.PaidUntil, migrated.PaidUntil);
            Assert.Equal(deadline, migrated.DueTicks);
            Assert.Equal(paid.Cycles, migrated.Cycles);
        }
        using var reopened = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var resumed = CapsuleerTestDriver.Miner(reopened, f.Clock);
        Assert.Equal(MinerActivity.Mining, resumed.Work.Activity);
        Assert.Equal(paid.Stamp + 1, resumed.Work.Stamp);
        Assert.Equal(deadline, resumed.Work.DueTicks);
        Assert.Equal(paid.Cycles, resumed.Work.Cycles);
        Assert.Equal(f.Clock.Now + deadline - resumed.Work.ActiveTicks, resumed.Work.PaidUntil);
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Equal(resumed.Work.PaidUntil, saved.Set<ModuleCooldown>().Find(paid.ModuleId)!.ReadyAt);
        f.Clock.Milliseconds += 50;
        using var metrics = SessionPerformance.Begin("scheduler not-due frame", _ => { }, force: true)!;
        Assert.Equal(0, reopened.GetRequiredService<ICapsuleerScheduler>().Run(2).Turn.Completed);
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
