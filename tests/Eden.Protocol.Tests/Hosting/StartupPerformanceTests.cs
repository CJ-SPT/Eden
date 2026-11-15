using System.Diagnostics;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.Server.Launcher;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Hosting;

public sealed class StartupPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void ReplaySavedWorldReadinessWithoutListeners()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_STARTUP_REPLAY_SOURCE");
        if (source is null)
        {
            output.WriteLine("Set EDEN_STARTUP_REPLAY_SOURCE to a frozen saved-world directory to measure startup.");
            return;
        }
        string directory = TestDirectory.Create("eden-startup-replay-").FullName;
        try
        {
            WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
            foreach (string file in new[] { "world.sqlite", "catalog.sqlite", "traffic.sqlite" })
            {
                string path =
                    file == "catalog.sqlite"
                        ? new WorldDatabase(Path.Combine(source, "world.sqlite")).CatalogPath
                        : Path.Combine(source, file);
                if (!File.Exists(path))
                {
                    continue;
                }
                using SqliteConnection original = new(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                    }.ToString()
                );
                using SqliteConnection target = new(
                    new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, file), Pooling = false }.ToString()
                );
                original.Open();
                target.Open();
                original.BackupDatabase(target);
            }
            TestClock clock = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
            long started = Stopwatch.GetTimestamp();
            using var metrics = SessionPerformance.Capture("startup.replay", Report);
            using (SessionPerformance.Phase("startup.reconciliation"))
            {
                WorldInitialization.Initialize(database, clock);
            }
            using (SessionPerformance.Phase("startup.population"))
            {
                Assert.Equal(0, WorldInitialization.PrepareCapsuleers(database, clock, maximumCommunities: 0).CreatedCommunities);
            }
            ServiceProvider services;
            using (SessionPerformance.Phase("startup.host"))
            {
                services = ServerServices.Build(database, clock, liveWorld: true);
            }
            using (services)
            {
                var world = services.GetRequiredService<LiveWorld>();
                using (SessionPerformance.Phase("startup.resident"))
                {
                    world.Economy.Warm();
                }
                using (SessionPerformance.Phase("startup.accounts"))
                {
                    services.GetRequiredService<LauncherStore>().Initialize();
                }
                ServerStartup.WarmCatalogs(services, database);
                Assert.True(world.Economy.IsLoaded);
                output.WriteLine($"Readiness without listeners/mod activation: {Stopwatch.GetElapsedTime(started).TotalSeconds:F3}s");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private void Report(PerformanceSample sample) =>
        output.WriteLine(
            $"{sample.Name}: {sample.Elapsed.TotalMilliseconds:F2}ms, commands={sample.Commands}, "
                + $"connections={sample.Connections}, fittings={sample.Fittings}, allocated={sample.AllocatedBytes}"
        );
}
