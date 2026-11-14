using System.Diagnostics;
using Eden.Data.World.Economy;
using Eden.Data.World.Mining;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Mining;

public sealed class ResourceRenewalTests(ITestOutputHelper output)
{
    private const int SystemId = 30000144;
    private const int FirstBelt = 40009232;
    private const int BeltCount = 16;
    private const int RocksPerBelt = 128;

    private static void Prepare(TestWorld db)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO ItemCategory(Id,Name) VALUES (25,'Asteroid');
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (9,'Asteroid Belt',2),(462,'Veldspar',25);
            INSERT INTO ItemType(Id,Name,GroupId,Radius) VALUES (15,'Asteroid Belt',9,1),(1230,'Veldspar',462,1);
            INSERT INTO SourceRecord VALUES ('client/types','1230','{{"volume":0.1}}');
            """
        );
        for (int index = 0; index < BeltCount; index++)
        {
            int belt = FirstBelt + index;
            catalog.Database.ExecuteSql(
                $"INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES ({belt},{SystemId},15,9,0,0,0)"
            );
            string key = belt.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string counts = $"{{\"1230\":{RocksPerBelt}}}";
            catalog.Database.ExecuteSql($"INSERT INTO SourceRecord VALUES ('client/asteroidcountbytypeidbeltid',{key},{counts})");
        }
    }

    [Fact]
    public void RepeatedGeometryPreparationUsesTwoQueriesForSixteenSavedBelts()
    {
        using TestWorld db = new();
        Prepare(db);
        Assert.Equal(BeltCount * RocksPerBelt, db.Services.GetRequiredService<IAsteroidField>().List(SystemId).Count);
        using WorldDbContext world = new(db.Path);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        _ = catalog.CelestialsInSystem(SystemId);
        using var metrics = SessionPerformance.Begin("saved belt geometry", _ => { }, force: true)!;
        AsteroidFieldStore.Populate(world, catalog, SystemId, DateTimeOffset.UtcNow.ToFileTime());
        Assert.Equal(2, metrics.Commands);
        Assert.False(world.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OverdueRenewalPreservesDepositsAcrossCheckpointAndRestart(bool resident, bool coldBeltCounts)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: resident);
        Prepare(db);
        var field = db.Services.GetRequiredService<IAsteroidField>();
        Assert.Equal(BeltCount * RocksPerBelt, field.List(SystemId).Count);
        WorldAsteroid[] original;
        using (WorldDbContext world = new(db.Path))
        {
            original = world.Set<WorldAsteroid>().AsNoTracking().OrderBy(a => a.Id).ToArray();
            world.Database.ExecuteSqlRaw("UPDATE WorldAsteroid SET Quantity=0");
            world.Database.ExecuteSql($"UPDATE WorldAsteroid SET Quantity={original[0].Quantity + 1},X=123 WHERE Id={original[0].Id}");
            world.Database.ExecuteSql($"DELETE FROM WorldAsteroid WHERE Id={original[1].Id}");
            clock.UtcNow = DateTimeOffset.FromFileTime(world.Set<SavedBeltRenewal>().Min(b => b.NextRenewal)).AddHours(6);
        }
        Assert.Single(field.List(SystemId));
        if (coldBeltCounts)
        {
            string path = new WorldDatabase(db.Path).CatalogPath;
            CatalogProvider.Invalidate(path);
            using CatalogDbContext catalog = new(path);
            catalog.Definitions.PreloadCore();
            _ = catalog.CelestialsInSystem(SystemId);
            _ = catalog.FittingType(1230);
            _ = catalog.Read(CatalogData.Client.Types, 1230L);
        }
        var renewal = db.Services.GetRequiredService<IResourceRenewal>();
        long started = Stopwatch.GetTimestamp();
        List<string> commands = [];
        using (var measured = SessionPerformance.Begin("overdue belt batch", output.WriteLine, force: true, command: commands.Add)!)
        {
            Assert.Equal(BeltCount, renewal.Renew());
            output.WriteLine($"Renewal {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms; commands={measured.Commands}.");
            if (resident)
            {
                Assert.DoesNotContain(commands, sql => sql.Contains("World", StringComparison.Ordinal));
                Assert.InRange(measured.Connections, 0, 1);
            }
            else
            {
                Assert.InRange(measured.Commands, 1, 6);
                Assert.InRange(measured.Connections, 1, 2);
            }
            Assert.Equal(0, measured.FittingCalculations);
        }
        Assert.Equal(BeltCount * RocksPerBelt, field.List(SystemId).Count);
        Assert.Equal(0, renewal.Renew());
        if (resident)
        {
            await LiveWorld.Find(db.Path)!.FlushAsync();
        }
        string checkpoint = Path.Combine(Path.GetDirectoryName(db.Path)!, "renewed.sqlite");
        File.Copy(db.Path, checkpoint);
        WorldInitialization.Initialize(new(checkpoint), clock);
        using WorldDbContext reopened = new(checkpoint);
        var saved = reopened.Set<WorldAsteroid>().OrderBy(a => a.Id).ToArray();
        Assert.Equal(original.Length, saved.Length);
        for (int index = 0; index < original.Length; index++)
        {
            Assert.Equal(original[index].Id, saved[index].Id);
            Assert.Equal(original[index].TypeId, saved[index].TypeId);
            Assert.Equal(original[index].Quantity + (index == 0 ? 1 : 0), saved[index].Quantity);
            Assert.Equal(index == 0 ? 123 : original[index].X, saved[index].X);
            Assert.Equal(original[index].Y, saved[index].Y);
            Assert.Equal(original[index].Z, saved[index].Z);
            Assert.Equal(original[index].Radius, saved[index].Radius);
        }
        Assert.All(
            reopened.Set<SavedBeltRenewal>(),
            belt =>
                Assert.Equal(
                    ResourceRenewalRules.Next(clock.Now, reopened.Set<SavedEconomicPolicy>().Single().RenewalHourUtc),
                    belt.NextRenewal
                )
        );
    }

    [Fact]
    public void FailedFileDeadlineUpdateRollsBackOreBeforeRetry()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        Prepare(db);
        var field = db.Services.GetRequiredService<IAsteroidField>();
        Assert.NotEmpty(field.List(SystemId));
        long deadline;
        using (WorldDbContext world = new(db.Path))
        {
            deadline = world.Set<SavedBeltRenewal>().Min(b => b.NextRenewal);
            world.Database.ExecuteSqlRaw(
                """
                UPDATE WorldAsteroid SET Quantity=0;
                CREATE TRIGGER RejectRenewal BEFORE UPDATE OF NextRenewal ON WorldBeltRenewal
                BEGIN SELECT RAISE(ABORT,'test renewal rollback'); END;
                """
            );
        }
        Assert.Empty(field.List(SystemId));
        clock.UtcNow = DateTimeOffset.FromFileTime(deadline);
        var renewal = db.Services.GetRequiredService<IResourceRenewal>();
        Assert.Throws<SqliteException>(() => renewal.Renew(1));
        Assert.Empty(field.List(SystemId));
        using (WorldDbContext world = new(db.Path))
        {
            Assert.All(world.Set<WorldAsteroid>(), rock => Assert.Equal(0, rock.Quantity));
            Assert.All(world.Set<SavedBeltRenewal>(), belt => Assert.Equal(deadline, belt.NextRenewal));
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectRenewal");
        }
        Assert.Equal(1, renewal.Renew(1));
        Assert.Equal(RocksPerBelt, field.List(SystemId).Count);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(BeltCount - 1, saved.Set<SavedBeltRenewal>().Count(belt => belt.NextRenewal == deadline));
        Assert.Throws<InvalidDataException>(() => renewal.Renew(0));
        Assert.Throws<InvalidDataException>(() => renewal.Renew(ResourceRenewalRules.BatchSize + 1));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(720)]
    public void StartupRenewsOverdueBeltsOnceAndLeavesLaterDeadlinesAlone(int offlineHours)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        Prepare(db);
        Assert.NotEmpty(db.Services.GetRequiredService<IAsteroidField>().List(SystemId));
        long deadline;
        using (WorldDbContext world = new(db.Path))
        {
            deadline = world.Set<SavedBeltRenewal>().Min(b => b.NextRenewal);
            world.Database.ExecuteSqlRaw("UPDATE WorldAsteroid SET Quantity=0");
        }
        clock.UtcNow = DateTimeOffset.FromFileTime(deadline - 1);
        WorldInitialization.Initialize(new(db.Path), clock);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.All(world.Set<WorldAsteroid>(), rock => Assert.Equal(0, rock.Quantity));
        }
        clock.AdvanceWallTime(TimeSpan.FromHours(offlineHours) + TimeSpan.FromTicks(1));
        long future = clock.Now + 1;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSql($"UPDATE WorldBeltRenewal SET NextRenewal={future} WHERE BeltId={FirstBelt}");
        }
        WorldInitialization.Initialize(new(db.Path), clock);
        WorldInitialization.Initialize(new(db.Path), clock);
        using WorldDbContext reopened = new(db.Path);
        Assert.Equal(BeltCount * RocksPerBelt, reopened.Set<WorldAsteroid>().Count());
        Assert.Equal(RocksPerBelt, reopened.Set<WorldAsteroid>().Count(rock => rock.Quantity == 0));
        Assert.All(reopened.Set<WorldAsteroid>().Where(rock => rock.BeltId != FirstBelt), rock => Assert.True(rock.Quantity > 0));
        Assert.Equal(future, reopened.Set<SavedBeltRenewal>().Find(FirstBelt)!.NextRenewal);
        Assert.All(reopened.Set<SavedBeltRenewal>(), belt => Assert.True(belt.NextRenewal > clock.Now));
    }
}
