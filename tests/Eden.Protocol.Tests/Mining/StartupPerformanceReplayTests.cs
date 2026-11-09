using System.Diagnostics;
using Eden.Data.World.Economy;
using Eden.Data.World.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Mining;

public sealed class StartupPerformanceReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void ReconcileSavedWorldAfterOfflineRenewal()
    {
        string? sourceDirectory = Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_SOURCE");
        if (sourceDirectory is null)
        {
            return;
        }
        string directory = TestDirectory.Create("eden-startup-measure-").FullName;
        string installed = Path.Combine(sourceDirectory, "world.sqlite");
        WorldDatabase sourceDatabase = new(installed);
        WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
        try
        {
            File.Copy(sourceDatabase.CatalogPath, database.CatalogPath);
            using (
                SqliteConnection source = new(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = installed,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                    }.ToString()
                )
            )
            using (
                SqliteConnection target = new(new SqliteConnectionStringBuilder { DataSource = database.Path, Pooling = false }.ToString())
            )
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
            }
            TestClock clock = new();
            int belts;
            int rocks;
            using (WorldDbContext world = new(database.Path))
            {
                clock.UtcNow = DateTimeOffset.FromFileTime(world.Set<SavedBeltRenewal>().Min(b => b.NextRenewal)).AddHours(6);
                belts = world.Set<SavedBeltRenewal>().Count();
                rocks = world.Set<WorldAsteroid>().Count();
            }
            long started = Stopwatch.GetTimestamp();
            using (SessionPerformance.Begin("startup.overdue", output.WriteLine, force: true))
            {
                WorldInitialization.Initialize(database, clock);
            }
            output.WriteLine(
                $"Installed save: belts={belts}, asteroids={rocks}, initialization={Stopwatch.GetElapsedTime(started).TotalSeconds:F3}s"
            );
            using (SessionPerformance.Begin("startup.current", output.WriteLine, force: true))
            {
                WorldInitialization.Initialize(database, clock);
            }
            using WorldDbContext reopened = new(database.Path);
            Assert.Equal(rocks, reopened.Set<WorldAsteroid>().Count());
            Assert.Equal(0, reopened.Set<SavedBeltRenewal>().Count(b => b.NextRenewal <= clock.Now));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
