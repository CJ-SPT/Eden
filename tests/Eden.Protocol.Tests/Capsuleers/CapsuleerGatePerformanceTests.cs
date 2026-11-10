using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerGatePerformanceTests(ITestOutputHelper output)
{
    private static readonly long[] ReportedPilots = [90001917, 90001849, 90001899, 90001872];
    private static readonly int[] ReportedSystems = [30003034, 30002056, 30004994];

    [Fact]
    public void UnchangedDroneResupplyCalculatesItsFitOnce()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        PassiveCatalogFixture.LoadCombatDrones(fixture.Database.CatalogPath);
        var founder = fixture.Founder();
        using var host = ServerServices.Build(fixture.Database, fixture.Clock);
        using WorldDbContext world = new(fixture.Database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        const int brutix = 16229;
        TrafficFitting.Apply(
            world,
            catalog,
            founder.PilotId,
            founder.FounderShipId,
            TrafficSnapshotTests.Profile() with
            {
                HullTypeId = brutix,
                Equipment = [],
            },
            fixture.Clock.Now
        );
        CapsuleerSupplyStore.Ensure(world, catalog, founder.PilotId, founder.FounderShipId);
        int stock = world.Set<Eden.Data.World.Inventory.OwnedItem>().Sum(item => item.Quantity);
        long wallet = world.Set<Eden.Data.World.Market.PilotWallet>().Find(founder.PilotId)!.Cents;
        using var metrics = SessionPerformance.Begin("unchanged drone resupply", output.WriteLine, force: true)!;
        var result = host.GetRequiredService<CapsuleerResupply>()
            .Dock(fixture.Database, world, catalog, founder.PilotId, null, fixture.Clock.Now);
        Assert.True(result.Checked);
        Assert.Empty(result.Requirements);
        Assert.Equal(0, result.SpentCents);
        Assert.Equal(1, metrics.FittingCalculations);
        Assert.Equal(stock, world.Set<Eden.Data.World.Inventory.OwnedItem>().Sum(item => item.Quantity));
        Assert.Equal(wallet, world.Set<Eden.Data.World.Market.PilotWallet>().Find(founder.PilotId)!.Cents);
    }

    [Fact]
    public void ResupplyOfAHullWithoutDroneSupportDoesNotCalculateFittings()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        long pilot = fixture.Founder().PilotId;
        using WorldDbContext world = new(fixture.Database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        int hull = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilot).ActiveShip.TypeId;
        Assert.False(Eden.Gameplay.Drones.DroneRules.SupportsHull(hull));
        using var metrics = SessionPerformance.Begin("non-drone resupply requirements", output.WriteLine, force: true)!;
        Assert.Empty(CapsuleerResupply.Requirements(fixture.Database, world, catalog, pilot, []));
        Assert.Equal(0, metrics.FittingCalculations);
        Assert.InRange(metrics.Commands, 1, 4);
        Assert.Equal(0, metrics.Connections);
    }

    [Fact]
    public void StarterDroneDiscoveryDoesNotPinTheEntireTypeCatalog()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PassiveCatalogFixture.LoadCombatDrones(fixture.Database.CatalogPath);
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        _ = catalog.Definitions.TypesInGroup(Eden.Gameplay.Drones.DroneRules.CombatDroneGroup);
        long pinned = catalog.Definitions.Statistics.PinnedRecords;
        Eden.Gameplay.Drones.DroneProfile[] drones;
        using (var cold = SessionPerformance.Begin("cold starter drone discovery", output.WriteLine, force: true)!)
        {
            drones = CapsuleerSupplyStore.AvailableDrones(catalog);
            Assert.InRange(cold.Commands, 1, 3);
            Assert.InRange(cold.Connections, 1, 3);
            Assert.Equal(0, cold.FittingCalculations);
        }
        Assert.NotEmpty(drones);
        Assert.Equal(pinned, catalog.Definitions.Statistics.PinnedRecords);
        using var metrics = SessionPerformance.Begin("warm starter drone discovery", output.WriteLine, force: true)!;
        Assert.Same(drones, CapsuleerSupplyStore.AvailableDrones(catalog));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void CopiedSaveProfilesReportedTrafficActions()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_GATE_PERFORMANCE_SOURCE");
        if (source is null)
        {
            output.WriteLine("Copied-save profiling is opt-in: set EDEN_GATE_PERFORMANCE_SOURCE to the reported world.sqlite.");
            return;
        }
        var directory = TestDirectory.Create("gate-performance-copy-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            File.Copy(new WorldDatabase(source).CatalogPath, database.CatalogPath);
            string traffic = Path.Combine(Path.GetDirectoryName(source)!, "traffic.sqlite");
            if (File.Exists(traffic))
            {
                File.Copy(traffic, Path.Combine(directory.FullName, "traffic.sqlite"));
            }
            using (SqliteConnection original = new($"Data Source={source};Mode=ReadOnly;Pooling=False"))
            using (SqliteConnection target = new($"Data Source={database.Path};Pooling=False"))
            {
                original.Open();
                target.Open();
                original.BackupDatabase(target);
            }
            TestClock clock = new();
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            _ = host.GetRequiredService<LiveWorld>();
            using (WorldDbContext saved = new(database.Path))
            {
                var jobs = saved.Set<SavedCapsuleerJob>().AsNoTracking().Where(j => ReportedPilots.Contains(j.PilotId)).ToArray();
                foreach (var row in jobs)
                {
                    var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(row.PilotId);
                    CapsuleerJob job = new(row.PilotId, row.Revision, row.Sequence, row.DueTicks, row.Action);
                    long activeTicks = hauler.Read()!.Work.ActiveTicks;
                    var startedAt = clock.UtcNow;
                    for (int step = 0; step < 15; step++)
                    {
                        using var operation = host.GetRequiredService<Eden.Infrastructure.Operations.IPilotOperationScope>().Begin();
                        using var cost = SessionPerformance.Begin($"pilot {job.PilotId} {job.Action}", output.WriteLine, force: true)!;
                        clock.UtcNow = startedAt + TimeSpan.FromTicks(Math.Max(0, job.DueTicks - activeTicks));
                        job = hauler.Execute(job, step, background: true);
                    }
                }
            }
            foreach (int system in ReportedSystems)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    using var metrics = SessionPerformance.Begin($"geometry {system} pass {pass}", output.WriteLine, force: true)!;
                    using WorldDbContext world = new(database.Path);
                    using var transaction = world.Database.BeginTransaction();
                    using CatalogDbContext catalog = new(database.CatalogPath);
                    var objects = CapsuleerGeometry.Load(world, catalog, system, clock.Now);
                    transaction.Commit();
                    output.WriteLine($"Objects={objects.Count}");
                }
            }
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            using var observer = host.GetRequiredService<IHumanPresence>().Select(90000001, 30002659);
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            for (int second = 0; second < 5; second++)
            {
                clock.Seconds++;
                using var metrics = SessionPerformance.Begin(
                    $"copied world second {second}",
                    line =>
                    {
                        if (double.Parse(line.Split(": ")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) >= 20)
                        {
                            output.WriteLine(line);
                        }
                    },
                    force: true
                )!;
                do
                {
                    worker.Tick();
                } while (scheduler.NextWake().Delay == TimeSpan.Zero);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TenfoldTrafficCompletesGateTransfersAndStationVisits()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        WorldConfigurationStore settings = new(fixture.Database, fixture.Clock);
        var configuration = settings.Read();
        settings.Save(
            configuration.Generation,
            configuration.Active.Draft with
            {
                Settings = configuration.Active.Draft.Settings with { Capsuleers = new(TrafficMultiplier: 10) },
            },
            "gate performance test"
        );
        Eden.World.Storage.WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var observer = host.GetRequiredService<IHumanPresence>().Select(90000001, 30000141);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var worker = host.GetRequiredService<CapsuleerMaintenance>();
        List<string> phases = [];
        using var metrics = SessionPerformance.Begin("tenfold gate replay", phases.Add, force: true)!;
        for (int second = 0; second < 180; second++)
        {
            fixture.Clock.Seconds++;
            worker.Tick();
            while (scheduler.NextWake().Delay == TimeSpan.Zero)
            {
                worker.Tick();
            }
        }
        Assert.True(scheduler.Jobs().Count > 10);
        Assert.Contains(phases, line => line.StartsWith("Performance traffic.completeJump:", StringComparison.Ordinal));
        Assert.Contains(phases, line => line.StartsWith("Performance traffic.tradeAndLaunch:", StringComparison.Ordinal));
        output.WriteLine($"Roster={scheduler.Jobs().Count}.");
        foreach (
            string phase in phases
                .OrderByDescending(line =>
                    double.Parse(line.Split(": ")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)
                )
                .Take(30)
        )
        {
            output.WriteLine(phase);
        }
        output.WriteLine($"Commands={metrics.Commands}, connections={metrics.Connections}, fits={metrics.FittingCalculations}.");
    }
}
