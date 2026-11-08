using System.Diagnostics;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
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
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerProvisioningScaleTests(ITestOutputHelper output)
{
    private sealed record SimulationWindow(int EndSecond, double Milliseconds, long CompletedActions);

    private sealed record Measurement(
        string Workload,
        CapsuleerPopulationSummary Population,
        double ProvisionMilliseconds,
        double HostMilliseconds,
        double HealthWarmMilliseconds,
        double CachedHealthMilliseconds,
        double FrameMilliseconds,
        double DueTurnMilliseconds,
        int DueActions,
        int NativeScenes,
        long ManagedBytes,
        double CaptureMilliseconds,
        double ProjectionMilliseconds,
        double WriteMilliseconds,
        IReadOnlyList<SimulationWindow> Windows
    );

    [Fact]
    public async Task InstalledCatalogProvisioningUsesAnIsolatedWorldAndRetainsWarmPopulationViews()
    {
        string? catalogPath = Environment.GetEnvironmentVariable("EDEN_PROVISION_CATALOG");
        if (string.IsNullOrWhiteSpace(catalogPath))
        {
            output.WriteLine("Installed-catalog diagnostic not requested; set EDEN_PROVISION_CATALOG to run it explicitly.");
            return;
        }
        var directory = TestDirectory.Create("capsuleer-installed-population-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            File.Copy(catalogPath, database.CatalogPath);
            TestClock clock = new();
            long started = Stopwatch.GetTimestamp();
            WorldInitialization.Initialize(database, clock, economy: new(false, false));
            double provision = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var population = new CapsuleerPopulationStore(database).Summary();
            Assert.True(population.EligibleSystems > 0);
            Assert.True(population.Agents >= population.EligibleSystems * CapsuleerProvisioningRules.FoundersPerCommunity);
            using (WorldDbContext world = new(database.Path))
            {
                Assert.All(
                    world.Set<SavedCapsuleerAgent>().GroupBy(a => a.CommunitySystemId).Select(g => g.Count()).ToArray(),
                    count => Assert.Equal(CapsuleerProvisioningRules.FoundersPerCommunity, count)
                );
            }
            await Measure(database, clock, provision, "installed catalog");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SyntheticPopulationRetainsBoundedQueueAndWarmViewsBeyondTheVisitorCacheLimit()
    {
        int systems = int.TryParse(Environment.GetEnvironmentVariable("EDEN_PROVISION_SYSTEMS"), out int count) ? count : 8;
        Assert.InRange(systems, 2, 5000);
        using CapsuleerHaulerTests.Fixture f = new();
        CapsuleerProvisioningTests.AddDestinationOre(f.Database);
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            var original = catalog.Set<Station>().First();
            catalog.Add(
                new Station
                {
                    Id = 64000000,
                    SolarSystemId = 30000144,
                    TypeId = original.TypeId,
                    OwnerId = original.OwnerId,
                    OperationId = original.OperationId,
                    OrbitId = original.OrbitId,
                    X = original.X,
                    Y = original.Y,
                    Z = original.Z,
                }
            );
            catalog.SaveChanges();
        }
        for (int index = 0; index < systems - 2; index++)
        {
            CapsuleerProvisioningTests.AddSystem(f.Database, 31000000 + index);
        }
        long started = Stopwatch.GetTimestamp();
        new CapsuleerProvisioner(f.Database, f.Clock).Reconcile();
        double provision = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.Equal(systems * CapsuleerProvisioningRules.FoundersPerCommunity, new CapsuleerPopulationStore(f.Database).Summary().Agents);
        await Measure(f.Database, f.Clock, provision, $"synthetic {systems} systems");
    }

    private async Task Measure(WorldDatabase database, TestClock clock, double provision, string workload)
    {
        var population = new CapsuleerPopulationStore(database);
        var summary = population.Summary();
        var communities = population.Communities().Select(c => c.SystemId).ToHashSet();
        using (CatalogDbContext catalog = new(database.CatalogPath))
        using (WorldDbContext world = new(database.Path))
        {
            var remote = catalog.Stations().First(s => !communities.Contains(s.SolarSystemId));
            var human = world.Pilots.Where(p => p.AccountId > 0).OrderBy(p => p.Id).First();
            world.Ships.Single(s => s.Id == human.ActiveShipId).StationId = remote.Id;
            world.SaveChanges();
        }
        long started = Stopwatch.GetTimestamp();
        using var host = ServerServices.Build(database, clock, liveWorld: true);
        double hostTime = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var jobs = scheduler.Jobs();
        Assert.Equal(summary.Agents, jobs.Count);
        var health = host.GetRequiredService<IPilotHealth>();
        started = Stopwatch.GetTimestamp();
        foreach (var job in jobs)
        {
            Assert.NotNull(health.Read(job.PilotId, clock.Now));
        }
        double warm = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        using (var metrics = SessionPerformance.Begin("all autonomous health views warmed", output.WriteLine, force: true)!)
        {
            foreach (var job in jobs)
            {
                Assert.NotNull(health.Read(job.PilotId, clock.Now));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        double cached = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        using var scope = host.CreateScope();
        var services = scope.ServiceProvider;
        var ships = services.GetRequiredService<SqliteWorld>();
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
        context.Select(ships.FindPilot(account)!);
        PilotSessionOutput sink = new(context, account, account, _ => Task.CompletedTask, _ => { });
        var updates = services.GetRequiredService<PilotUpdateCoordinator>();
        updates.Enter(context);
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
        started = Stopwatch.GetTimestamp();
        using (var metrics = SessionPerformance.Begin("global population docked observer frames", output.WriteLine, force: true)!)
        {
            for (int i = 0; i < 10; i++)
            {
                await tick.AdvanceAsync(context, sink, _ => { }, account, account);
                _ = host.GetRequiredService<ICapsuleerDiagnostics>().Read();
                _ = host.GetRequiredService<ICapsuleerPopulation>().Summary();
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        double frames = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        clock.Milliseconds += 1000;
        started = Stopwatch.GetTimestamp();
        var turn = scheduler.Run(context.Flight.CurrentStamp);
        double due = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.InRange(turn.Turn.Completed, 1, CapsuleerJobQueue.ActionsPerTurn);
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        Assert.Equal(0, host.GetRequiredService<ICapsuleerScene>().MaterializedPilots);
        while (scheduler.Run(context.Flight.CurrentStamp).Turn.OldestDueTicks is not null)
        {
            // Finish the fixed one-second window before disposing its selected session.
        }
        List<SimulationWindow> windows = [];
        if (int.TryParse(Environment.GetEnvironmentVariable("EDEN_PROVISION_SECONDS"), out int seconds))
        {
            Assert.InRange(seconds, 1, 120);
            long sustainStarted = Stopwatch.GetTimestamp();
            for (int second = 2; second <= seconds; second++)
            {
                clock.Seconds = second;
                long windowStarted = Stopwatch.GetTimestamp();
                long completed = host.GetRequiredService<ICapsuleerDiagnostics>().Read().CompletedActions;
                using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
                await host.GetRequiredService<ICapsuleerHandoff>().SynchronizeAsync(() => { }, cancellation.Token);
                windows.Add(
                    new(
                        second,
                        Stopwatch.GetElapsedTime(windowStarted).TotalMilliseconds,
                        host.GetRequiredService<ICapsuleerDiagnostics>().Read().CompletedActions - completed
                    )
                );
            }
            var diagnostics = host.GetRequiredService<ICapsuleerDiagnostics>().Read();
            Assert.Equal(0, diagnostics.Failures);
            Assert.Equal(0, diagnostics.DueJobs);
            output.WriteLine(
                $"Sustained {seconds}s: {Stopwatch.GetElapsedTime(sustainStarted).TotalSeconds:F3}s real, "
                    + $"{diagnostics.CompletedActions} actions, {diagnostics.Jobs} jobs."
            );
        }
        var live = host.GetRequiredService<LiveWorld>();
        await live.FlushAsync();
        var checkpoint = live.LastCheckpoint!;
        Measurement measurement = new(
            workload,
            summary,
            provision,
            hostTime,
            warm,
            cached,
            frames,
            due,
            turn.Turn.Completed,
            host.GetRequiredService<ICapsuleerScene>().MaterializedPilots,
            GC.GetTotalMemory(false),
            checkpoint.Capture.TotalMilliseconds,
            checkpoint.Projection.TotalMilliseconds,
            checkpoint.Write.TotalMilliseconds,
            windows
        );
        string document = JsonSerializer.Serialize(measurement, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(document);
        string? result = Environment.GetEnvironmentVariable("EDEN_PROVISION_OUTPUT");
        if (!string.IsNullOrWhiteSpace(result))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(result)!);
            await File.WriteAllTextAsync(result, document);
        }
    }
}
