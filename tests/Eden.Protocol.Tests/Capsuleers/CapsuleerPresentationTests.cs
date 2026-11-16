using System.Diagnostics;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Combat;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerPresentationTests(ITestOutputHelper output)
{
    private const int Source = 30000141;
    private const int Other = 31000001;

    [Fact]
    public void CommitDuringCaptureCannotHideDamageInTheNextSnapshot()
    {
        using PresentationFixture fixture = new();
        CallbackHealth health = new(fixture.Services.GetRequiredService<IPilotHealth>());
        var miners = ActivatorUtilities.CreateInstance<CapsuleerMinerFactory>(fixture.Services, health);
        var haulers = ActivatorUtilities.CreateInstance<CapsuleerHaulerFactory>(fixture.Services, health);
        var scheduler = ActivatorUtilities.CreateInstance<CapsuleerScheduler>(fixture.Services, miners, haulers);
        scheduler.Run(0);
        scheduler.Capture();
        var before = scheduler.Capture();
        var miner = Assert.Single(before.Miners);
        long hauler = Assert.Single(before.Haulers).Pilot.PilotId;
        bool committed = false;
        health.OnRead = pilot =>
        {
            if (pilot != hauler || committed)
            {
                return;
            }
            committed = true;
            using WorldActionScope action = new(fixture.World.Database, new(pilots: [miner.Pilot.PilotId]));
            PilotHealthStore.Apply(
                action.World,
                miner.Pilot.ShipId,
                miner.Health!.Defense,
                new DamageVector(1, 0, 0, 0),
                fixture.World.Clock.Now
            );
            action.Commit();
        };
        LiveWorld.CatalogChanged(fixture.World.Database.CatalogPath);
        var during = scheduler.Capture();
        Assert.True(committed);
        Assert.Equal(miner.Health, Assert.Single(during.Miners).Health);
        var after = scheduler.Capture();
        Assert.True(after.Revision > during.Revision);
        Assert.True(Assert.Single(after.Miners).Health!.Remaining.Shield < miner.Health!.Remaining.Shield);
        Assert.Same(after, scheduler.Capture());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameTickDamagePublishesOnlyAfterCommitAndRechargeRemainsExact(bool commit)
    {
        using PresentationFixture fixture = new();
        var before = fixture.Scheduler.Capture();
        var miner = Assert.Single(before.Miners);
        var health = Assert.IsType<ShipHealth>(miner.Health);
        using (WorldActionScope action = new(fixture.World.Database, new(pilots: [miner.Pilot.PilotId])))
        {
            PilotHealthStore.Apply(action.World, miner.Pilot.ShipId, health.Defense, new DamageVector(1, 0, 0, 0), fixture.World.Clock.Now);
            Assert.Same(before, fixture.Scheduler.Capture());
            if (commit)
            {
                action.Commit();
            }
        }
        var after = fixture.Scheduler.Capture();
        if (!commit)
        {
            Assert.Same(before, after);
            return;
        }
        Assert.True(after.Revision > before.Revision);
        double shield = Assert.Single(after.Miners).Health!.Remaining.Shield;
        Assert.True(shield < health.Remaining.Shield);
        long reads = fixture.Scheduler.PresentationReads;
        Assert.Same(after, fixture.Scheduler.Capture());
        Assert.Equal(reads, fixture.Scheduler.PresentationReads);
        fixture.World.Clock.Milliseconds += 100;
        var recharged = fixture.Scheduler.Capture();
        Assert.True(recharged.Revision > after.Revision);
        Assert.True(Assert.Single(recharged.Miners).Health!.Remaining.Shield > shield);
        Assert.Equal(miner.Work, Assert.Single(recharged.Miners).Work);
    }

    [Fact]
    public void CachedHealthHonorsTimedEffectsAndPaidRepairDeadlinesWithoutAdvancingJobs()
    {
        using PresentationFixture fixture = new();
        var miner = Assert.Single(fixture.Scheduler.Capture().Miners);
        var health = Assert.IsType<ShipHealth>(miner.Health);
        long now = fixture.World.Clock.Now;
        long expires = now + TimeSpan.FromMilliseconds(50).Ticks;
        long completes = now + TimeSpan.FromMilliseconds(100).Ticks;
        long module;
        using (WorldActionScope action = new(fixture.World.Database, new(pilots: [miner.Pilot.PilotId])))
        {
            module = action.World.Set<OwnedItem>().First(item => item.OwnerId == miner.Pilot.PilotId).Id;
            action.World.Add(
                new ShipHealthState
                {
                    ShipId = miner.Pilot.ShipId,
                    ShieldCharge = health.Defense.Shield,
                    ArmorDamage = 10,
                    UpdatedAt = now,
                }
            );
            action.World.Add(
                new PendingLocalRepair
                {
                    ModuleId = module,
                    ShipId = miner.Pilot.ShipId,
                    Amount = 10,
                    Layer = RepairLayer.Armor,
                    StartedAt = now,
                    CompletesAt = completes,
                }
            );
            action.World.Add(
                new ActiveFittedModule
                {
                    ModuleId = module,
                    ShipId = miner.Pilot.ShipId,
                    ExpiresAt = expires,
                }
            );
            action.Commit();
        }
        var jobs = fixture.Scheduler.Jobs();
        var damaged = fixture.Scheduler.Capture();
        Assert.Equal(health.Defense.Armor - 10, Assert.Single(damaged.Miners).Health!.Remaining.Armor);
        long reads = fixture.Scheduler.PresentationReads;
        fixture.World.Clock.Milliseconds += 49;
        Assert.Equal(damaged.Revision, fixture.Scheduler.Capture().Revision);
        Assert.Equal(reads, fixture.Scheduler.PresentationReads);
        fixture.World.Clock.Milliseconds++;
        fixture.Scheduler.Capture();
        Assert.True(fixture.Scheduler.PresentationReads > reads);
        Assert.Null(fixture.Live.Economy.Inspect(world => world.Set<ActiveFittedModule>().Find(module)));
        fixture.World.Clock.Milliseconds += 49;
        Assert.Equal(health.Defense.Armor - 10, Assert.Single(fixture.Scheduler.Capture().Miners).Health!.Remaining.Armor);
        fixture.World.Clock.Milliseconds++;
        var repaired = fixture.Scheduler.Capture();
        Assert.True(repaired.Revision > damaged.Revision);
        Assert.Equal(health.Defense.Armor, Assert.Single(repaired.Miners).Health!.Remaining.Armor);
        Assert.Same(repaired, fixture.Scheduler.Capture());
        Assert.Equal(jobs, fixture.Scheduler.Jobs());
    }

    [Fact]
    public void LocalMovementReusesAnotherSystemsObservations()
    {
        using CapsuleerHaulerTests.Fixture world = new(legacyFounders: false, isolateRoute: true);
        CapsuleerProvisioningTests.AddSystem(world.Database, Other);
        using var host = ServerServices.Build(world.Database, world.Clock, liveWorld: true);
        var ships = host.GetRequiredService<SqliteWorld>();
        var human = ships.FindPilot(1000002)!;
        SpacePosition original = new(1000000, 0, 0);
        ships.SetPosition(human.ShipId, original, Other);
        using var first = host.GetRequiredService<IHumanPresence>().Select(90000001, Source);
        using var second = host.GetRequiredService<IHumanPresence>().Select(human.PilotId, Other);
        host.PrepareActivatedPopulations();
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        scheduler.Run(0);
        scheduler.Capture();
        var before = scheduler.Capture();
        long reads = scheduler.PresentationReads;
        var live = host.GetRequiredService<LiveWorld>();
        var sourceVersion = live.PresentationVersion(Source);
        var otherVersion = live.PresentationVersion(Other);
        using (live.UseSystem(Other))
        {
            ships.SetPosition(human.ShipId, original + new SpacePosition(10, 0, 0), Other);
        }
        output.WriteLine($"Source {sourceVersion} -> {live.PresentationVersion(Source)}");
        output.WriteLine($"Other {otherVersion} -> {live.PresentationVersion(Other)}");
        var after = scheduler.Capture();
        int affected =
            before.Miners.Count(miner => miner.Pilot.SystemId == Other) + before.Haulers.Count(hauler => hauler.Pilot.SystemId == Other);
        Assert.True(affected > 0);
        Assert.Equal(affected, scheduler.PresentationReads - reads);
        foreach (var miner in before.Miners.Where(miner => miner.Pilot.SystemId == Source))
        {
            Assert.Same(miner, after.Miners.Single(value => value.Pilot.PilotId == miner.Pilot.PilotId));
        }
        foreach (var hauler in before.Haulers.Where(hauler => hauler.Pilot.SystemId == Source))
        {
            Assert.Same(hauler, after.Haulers.Single(value => value.Pilot.PilotId == hauler.Pilot.PilotId));
        }
    }

    [Theory]
    [InlineData(false, 1, 1)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 4, 10)]
    public async Task UnchangedPresentationSharesObservationsAcrossAdvancingFrames(bool occupied, int observers, int multiplier)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        WorldConfigurationStore settings = new(fixture.Database, fixture.Clock);
        var configuration = settings.Read();
        settings.Save(
            configuration.Generation,
            configuration.Active.Draft with
            {
                Settings = configuration.Active.Draft.Settings with { Capsuleers = new(TrafficMultiplier: multiplier, GateVisitors: 0) },
            },
            "presentation measurement"
        );
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var presence = host.GetRequiredService<IHumanPresence>();
        var humans = Enumerable
            .Range(0, observers)
            .Select(index => presence.Select(90000001 + index, occupied ? 30000141 : 30000144))
            .ToArray();
        try
        {
            host.PrepareActivatedPopulations();
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            worker.Tick();
            _ = scheduler.Capture();
            var before = scheduler.Capture();
            var jobs = scheduler.Jobs();
            long reads = scheduler.PresentationReads;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            using (var metrics = SessionPerformance.Begin("unchanged presentation frames", output.WriteLine, force: true)!)
            {
                for (int frame = 0; frame < 200; frame++)
                {
                    fixture.Clock.Milliseconds++;
                    worker.Tick();
                    for (int observer = 0; observer < observers; observer++)
                    {
                        Assert.Equal(before.Revision, scheduler.Capture().Revision);
                    }
                }
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            long totalAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            output.WriteLine(
                $"PRESENTATION occupied={occupied}, observers={observers}, multiplier={multiplier}, "
                    + $"pilots={before.Miners.Count + before.Haulers.Count}, reads={scheduler.PresentationReads - reads}, "
                    + $"allocated={totalAllocated}, ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}"
            );
            Assert.Equal(reads, scheduler.PresentationReads);
            Assert.Equal(jobs, scheduler.Jobs());
            long allocationBudget = (observers == 1 ? 6 : 10) * 1024L * 1024;
            Assert.InRange(totalAllocated, 0, allocationBudget);
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            await live.FlushAsync();
            var checkpoint = Assert.IsType<WorldCheckpoint>(live.LastCheckpoint);
            output.WriteLine(
                $"CHECKPOINT capture={checkpoint.Capture.TotalMilliseconds:F2}, "
                    + $"projection={checkpoint.Projection.TotalMilliseconds:F2}, write={checkpoint.Write.TotalMilliseconds:F2}"
            );
        }
        finally
        {
            foreach (var human in humans)
            {
                human.Dispose();
            }
        }
    }

    private sealed class PresentationFixture : IDisposable
    {
        internal CapsuleerHaulerTests.Fixture World { get; } = new(isolateRoute: true);
        private readonly ServiceProvider _host;
        internal ServiceProvider Services => _host;
        private readonly IHumanPresenceLease _human;
        internal CapsuleerScheduler Scheduler { get; }
        internal LiveWorld Live => _host.GetRequiredService<LiveWorld>();

        internal PresentationFixture()
        {
            _host = ServerServices.Build(World.Database, World.Clock, liveWorld: true);
            _human = _host.GetRequiredService<IHumanPresence>().Select(90000001, Source);
            Scheduler = _host.GetRequiredService<CapsuleerScheduler>();
            Scheduler.Run(0);
            Scheduler.Capture();
            Scheduler.Capture();
        }

        public void Dispose()
        {
            _human.Dispose();
            _host.Dispose();
            World.Dispose();
        }
    }

    private sealed class CallbackHealth(IPilotHealth inner) : IPilotHealth
    {
        internal Action<long>? OnRead { get; set; }

        public ShipHealth Read(long pilotId, long now)
        {
            var result = inner.Read(pilotId, now);
            OnRead?.Invoke(pilotId);
            return result;
        }

        public bool WasDestroyed(long shipId) => inner.WasDestroyed(shipId);
    }
}
