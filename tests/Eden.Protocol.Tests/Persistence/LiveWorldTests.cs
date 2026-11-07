using System.Diagnostics;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Flight;
using Eden.Server;
using Eden.Server.Launcher;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class LiveWorldTests
{
    private static long DiskValue(string path, string sql)
    {
        using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task CommittedStateIsImmediatelyVisibleInMemoryAndSavedAtFifteenSeconds()
    {
        using TestWorld db = new();
        TestClock simulation = new();
        CheckpointClock clock = new(simulation);
        using LiveWorld live = new(new(db.Path), clock, _ => { });
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='Resident pilot' WHERE Id=90000001");
        }
        using (WorldDbContext reader = new(db.Path))
        {
            Assert.Equal("Resident pilot", reader.Pilots.Single(p => p.Id == 90000001).Name);
        }
        const string changed = "SELECT COUNT(*) FROM WorldPilot WHERE Name='Resident pilot'";
        Assert.Equal(0, DiskValue(db.Path, changed));
        simulation.Seconds = 14;
        clock.FireDueTimers();
        await live.PendingSave;
        Assert.Null(live.LastCheckpoint);
        Assert.Equal(0, DiskValue(db.Path, changed));
        simulation.Seconds = 15;
        clock.FireDueTimers();
        await live.PendingSave;
        Assert.NotNull(live.LastCheckpoint);
        Assert.Equal(1, DiskValue(db.Path, changed));
        Assert.Equal(0, DiskValue(db.Path, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        var saved = live.LastCheckpoint;
        simulation.Seconds = 30;
        clock.FireDueTimers();
        await live.PendingSave;
        Assert.Same(saved, live.LastCheckpoint);
    }

    [Fact]
    public async Task RollbackIsInvisibleAndShutdownPersistsCommittedStateForReopening()
    {
        using TestWorld db = new();
        using (LiveWorld live = new(new(db.Path), new TestClock(), _ => { }))
        {
            using (WorldDbContext world = new(db.Path))
            using (var transaction = world.Database.BeginTransaction())
            {
                world.Pilots.Single(p => p.Id == 90000001).Name = "Rolled back";
                world.SaveChanges();
                transaction.Rollback();
            }
            await live.FlushAsync();
            Assert.Null(live.LastCheckpoint);
            using WorldDbContext committed = new(db.Path);
            committed.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='Shutdown saved' WHERE Id=90000001");
        }
        Assert.Equal(1, DiskValue(db.Path, "SELECT COUNT(*) FROM WorldPilot WHERE Name='Shutdown saved'"));
        using LiveWorld reopened = new(new(db.Path), new TestClock(), _ => { });
        using WorldDbContext reader = new(db.Path);
        Assert.Equal("Shutdown saved", reader.Pilots.Single(p => p.Id == 90000001).Name);
        Assert.Null(reopened.LastCheckpoint);
    }

    [Fact]
    public async Task SlowCheckpointDoesNotBlockNewWorldTransactionsOrOverwriteTheirChanges()
    {
        using TestWorld db = new();
        using ManualResetEventSlim captured = new();
        using ManualResetEventSlim release = new();
        int writes = 0;
        using LiveWorld live = new(
            new(db.Path),
            new TestClock(),
            _ => { },
            (snapshot, path) =>
            {
                if (Interlocked.Increment(ref writes) == 1)
                {
                    captured.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }
                LiveWorld.PersistSnapshot(snapshot, path);
            }
        );
        using (WorldDbContext first = new(db.Path))
        {
            first.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='First snapshot' WHERE Id=90000001");
        }
        Task saving = live.FlushAsync();
        try
        {
            Assert.True(captured.Wait(TimeSpan.FromSeconds(10)));
            await Task.Run(() =>
                {
                    using WorldDbContext second = new(db.Path);
                    second.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='Changed during save' WHERE Id=90000001");
                })
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
        }
        await saving;
        Assert.Equal(1, DiskValue(db.Path, "SELECT COUNT(*) FROM WorldPilot WHERE Name='First snapshot'"));
        using (WorldDbContext reader = new(db.Path))
        {
            Assert.Equal("Changed during save", reader.Pilots.Single(p => p.Id == 90000001).Name);
        }
        await live.FlushAsync();
        Assert.Equal(2, writes);
        Assert.Equal(1, DiskValue(db.Path, "SELECT COUNT(*) FROM WorldPilot WHERE Name='Changed during save'"));
    }

    [Fact]
    public async Task FailedSaveRetainsDirtyStateAndLastCompletedCheckpointThenRetries()
    {
        using TestWorld db = new();
        bool fail = false;
        TestClock simulation = new();
        CheckpointClock clock = new(simulation);
        using LiveWorld live = new(
            new(db.Path),
            clock,
            _ => { },
            (snapshot, path) =>
            {
                if (fail)
                {
                    throw new IOException("Injected checkpoint failure");
                }
                LiveWorld.PersistSnapshot(snapshot, path);
            }
        );
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='Saved checkpoint' WHERE Id=90000001");
        }
        await live.FlushAsync();
        var completed = live.LastCheckpoint;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='Pending checkpoint' WHERE Id=90000001");
        }
        fail = true;
        simulation.Seconds = 15;
        clock.FireDueTimers();
        await live.PendingSave;
        await Assert.ThrowsAsync<IOException>(() => live.FlushAsync());
        Assert.Same(completed, live.LastCheckpoint);
        Assert.IsType<IOException>(live.LastSaveFailure);
        Assert.Equal(1, DiskValue(db.Path, "SELECT COUNT(*) FROM WorldPilot WHERE Name='Saved checkpoint'"));
        fail = false;
        simulation.Seconds = 30;
        clock.FireDueTimers();
        await live.PendingSave;
        Assert.Null(live.LastSaveFailure);
        Assert.Equal(1, DiskValue(db.Path, "SELECT COUNT(*) FROM WorldPilot WHERE Name='Pending checkpoint'"));
    }

    [Fact]
    public async Task ConcurrentTransactionsDoNotLoseWalletUpdates()
    {
        using TestWorld db = new();
        using LiveWorld live = new(new(db.Path), new TestClock(), _ => { });
        using (WorldDbContext seed = new(db.Path))
        {
            seed.Add(new PilotWallet { PilotId = 90000001, Cents = 0 });
            seed.SaveChanges();
        }
        await Task.WhenAll(
            Enumerable
                .Range(0, 10)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        using WorldDbContext world = new(db.Path);
                        using var transaction = world.Database.BeginTransaction();
                        world.Set<PilotWallet>().Single(w => w.PilotId == 90000001).Cents++;
                        world.SaveChanges();
                        transaction.Commit();
                    })
                )
        );
        await live.FlushAsync();
        Assert.Equal(10, DiskValue(db.Path, "SELECT Cents FROM WorldWallet WHERE PilotId=90000001"));
        Assert.Equal(0, DiskValue(db.Path, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public void DuplicateOwnersAreRejectedAndTheWorldCanReopenAfterDisposal()
    {
        using TestWorld db = new();
        using (LiveWorld live = new(new(db.Path), new TestClock(), _ => { }))
        {
            Assert.Throws<IOException>(() => new LiveWorld(new(db.Path), new TestClock(), _ => { }));
        }
        using LiveWorld reopened = new(new(db.Path), new TestClock(), _ => { });
        Assert.Same(reopened, LiveWorld.Find(db.Path));
    }

    [Fact]
    public void AuthenticationMutationsAreDurableBeforeReturning()
    {
        using TestWorld db = new();
        using LiveWorld live = new(new(db.Path), new TestClock(), _ => { });
        LauncherStore launcher = new(new(db.Path), TimeProvider.System);
        launcher.Initialize();
        var account = launcher.Register(new("Resident account", "resident test password"));
        Assert.Equal(1, DiskValue(db.Path, $"SELECT COUNT(*) FROM LauncherCredential WHERE AccountId={account.AccountId}"));
        var session = launcher.SignIn(new("Resident account", "resident test password", true));
        string ticket = launcher.IssueTicket(session.AccessToken);
        Assert.Equal(1, DiskValue(db.Path, "SELECT COUNT(*) FROM LauncherTicket"));
        Assert.Equal(account.AccountId, launcher.ConsumeTicket(ticket));
        Assert.Equal(0, DiskValue(db.Path, "SELECT COUNT(*) FROM LauncherTicket"));
        launcher.Revoke(session.AccessToken);
        Assert.Equal(1, DiskValue(db.Path, "SELECT Revoked FROM LauncherSession"));
        Assert.Throws<LauncherException>(() => launcher.IssueTicket(session.AccessToken));
    }

    [Fact]
    public async Task FailedAtomicInstallKeepsOriginalCheckpointAndCleansStagingFile()
    {
        using TestWorld db = new();
        using LiveWorld live = new(new(db.Path), new TestClock(), _ => { });
        using (WorldDbContext first = new(db.Path))
        {
            first.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='First atomic save' WHERE Id=90000001");
        }
        await live.FlushAsync();
        string previous = db.Path + ".previous";
        File.Delete(previous);
        Directory.CreateDirectory(previous);
        try
        {
            using (WorldDbContext second = new(db.Path))
            {
                second.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='Second atomic save' WHERE Id=90000001");
            }
            Exception? error = await Record.ExceptionAsync(live.FlushAsync);
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.Equal(1, DiskValue(db.Path, "SELECT COUNT(*) FROM WorldPilot WHERE Name='First atomic save'"));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(db.Path)!, "*.save-*"));
        }
        finally
        {
            Directory.Delete(previous);
        }
        await live.FlushAsync();
        Assert.Equal(1, DiskValue(db.Path, "SELECT COUNT(*) FROM WorldPilot WHERE Name='Second atomic save'"));
    }

    [Fact]
    public async Task RecoveryFromCompletedCheckpointRestoresRelatedRowsAndExcludesUnsavedChanges()
    {
        using TestWorld db = new();
        using LiveWorld live = new(new(db.Path), new TestClock(), _ => { });
        const long transferredItem = 8000000000000;
        using (WorldDbContext world = new(db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Add(new PilotWallet { PilotId = 90000001, Cents = 100 });
            world.Add(
                new OwnedItem
                {
                    Id = transferredItem,
                    OwnerId = 90000001,
                    TypeId = 34,
                    LocationId = 60003760,
                    FlagId = 4,
                    Quantity = 1,
                }
            );
            world.SaveChanges();
            transaction.Commit();
        }
        await live.FlushAsync();
        using (WorldDbContext world = new(db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Set<PilotWallet>().Single(w => w.PilotId == 90000001).Cents = 200;
            world.Set<OwnedItem>().Find(transferredItem)!.OwnerId = 90000002;
            world.SaveChanges();
            transaction.Commit();
        }
        string recovery = Path.Combine(Path.GetDirectoryName(db.Path)!, "recovery.sqlite");
        File.Copy(db.Path, recovery);
        using LiveWorld restarted = new(new(recovery), new TestClock(), _ => { });
        using WorldDbContext reader = new(recovery);
        Assert.Equal(100, reader.Set<PilotWallet>().Single(w => w.PilotId == 90000001).Cents);
        Assert.Equal(90000001, reader.Set<OwnedItem>().Find(transferredItem)!.OwnerId);
        Assert.Equal(0, DiskValue(recovery, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public void ProductionProviderUsesInitializedWorldInMemoryAndFlushesOnShutdown()
    {
        using TestWorld catalogSource = new();
        string directory = TestDirectory.Create("eden-resident-startup-").FullName;
        WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
        File.Copy(new WorldDatabase(catalogSource.Path).CatalogPath, database.CatalogPath);
        try
        {
            // Reconciliation precedes the resident owner, as in production startup.
            WorldInitialization.Initialize(database);
            using (var host = ServerServices.Build(database, liveWorld: true))
            {
                var store = host.GetRequiredService<SqliteWorld>();
                var pilot = store.FindPilot(1000001)!;
                store.SetPosition(pilot.ShipId, new SpacePosition(1000, 2000, 3000), 30000142);
                Assert.Equal(0, DiskValue(database.Path, $"SELECT COUNT(*) FROM WorldPosition WHERE ShipId={pilot.ShipId}"));
                Assert.NotNull(LiveWorld.Find(database.Path));
            }
            Assert.Null(LiveWorld.Find(database.Path));
            var reopened = WorldInitialization.Open(database);
            Assert.Equal(new SpacePosition(1000, 2000, 3000), reopened.FindPilot(1000001)!.Position);
            Assert.Equal(0, DiskValue(database.Path, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DevelopmentTicketPreparationDoesNotAcquireTheRunningWorld()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using TestWorld db = new();
        using LiveWorld live = new(new(db.Path), new TestClock(), _ => { });
        using Process preparation = new()
        {
            StartInfo = new("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    typeof(ServerServices).Assembly.Location,
                    "--prepare-login",
                    Path.Combine(Path.GetDirectoryName(db.Path)!, "unprepared-client"),
                    "--world",
                    db.Path,
                    "--development-login",
                },
            },
        };
        Assert.True(preparation.Start());
        Task<string> stdout = preparation.StandardOutput.ReadToEndAsync();
        Task<string> stderr = preparation.StandardError.ReadToEndAsync();
        try
        {
            await preparation.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!preparation.HasExited)
            {
                preparation.Kill(entireProcessTree: true);
            }
        }
        Assert.NotEqual(0, preparation.ExitCode);
        Assert.Contains("Prepare the isolated development copy first.", await stderr);
        Assert.DoesNotContain("World checkpoint", await stdout);
        Assert.Same(live, LiveWorld.Find(db.Path));
        using WorldDbContext reader = new(db.Path);
        Assert.NotEmpty(reader.Pilots);
    }
}
