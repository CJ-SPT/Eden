using Eden.Protocol.Tests.Persistence;
using Eden.Server.Launcher;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Launcher;

public sealed class LauncherCleanupTests
{
    private const string Password = "cleanup test password";

    private static int Count(string path, string table, bool disk = false)
    {
        using var db = disk ? WorldConnections.FileConnection(path) : WorldConnections.Create(path);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public async Task ActiveRefreshReplaySurvivesCleanupAndRevokedFamiliesAreRemoved()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        LauncherStore store = new(new(world.Path), clock);
        store.Initialize();
        store.Register(new("Cleanup pilot", Password));
        var session = store.SignIn(new("Cleanup pilot", Password, true));
        await store.IssueTicketAsync(session.AccessToken);
        var rotated = await Task.Run(() => store.Refresh(session.RefreshToken!));
        clock.Seconds = LauncherStore.TicketSeconds;
        Assert.Equal(new LauncherCleanup(1, 0, 0), store.CleanupExpired());
        Assert.Equal(2, Count(world.Path, "LauncherRefresh"));
        Assert.Throws<LauncherException>(() => store.Refresh(session.RefreshToken!));
        Assert.Equal(new LauncherCleanup(0, 2, 1), store.CleanupExpired());
        Assert.Throws<LauncherException>(() => store.Refresh(rotated.RefreshToken!));
        Assert.Equal(0, Count(world.Path, "LauncherSession"));
        Assert.Equal(1, Count(world.Path, "LauncherCredential"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializationRemovesExpiredFamiliesAndRetainsActiveSessionsAcrossReopen(bool resident)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: resident);
        LauncherStore store = new(new(world.Path), clock);
        store.Initialize();
        store.Register(new("Reopen pilot", Password));
        var old = store.SignIn(new("Reopen pilot", Password, true));
        await store.IssueTicketAsync(old.AccessToken);
        clock.AdvanceWallTime(TimeSpan.FromDays(31));
        var current = store.SignIn(new("Reopen pilot", Password, true));
        world.Services.Dispose();
        WorldDatabase database = new(world.Path);
        using LiveWorld? restarted = resident ? new(database, clock, _ => { }) : null;
        LauncherStore reopened = new(database, clock);
        reopened.Initialize();
        reopened.Initialize();
        Assert.Equal(1, Count(world.Path, "LauncherSession"));
        Assert.Equal(1, Count(world.Path, "LauncherRefresh"));
        Assert.Equal(0, Count(world.Path, "LauncherTicket"));
        Assert.NotNull(await reopened.IssueTicketAsync(current.AccessToken));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(LauncherStore.AccessSeconds));
        await Assert.ThrowsAsync<LauncherException>(() => reopened.IssueTicketAsync(current.AccessToken));
        var renewed = reopened.Refresh(current.RefreshToken!);
        Assert.Equal(current.AccountId, renewed.AccountId);
        Assert.NotEqual(current.RefreshToken, renewed.RefreshToken);
        Assert.NotNull(await reopened.IssueTicketAsync(renewed.AccessToken));
        Assert.Equal(2, Count(world.Path, "WorldPilot"));
    }

    private static void SeedTerminalFamily(string path, int children)
    {
        using WorldDbContext saved = new(path);
        saved.Database.ExecuteSqlRaw(
            """
            INSERT INTO LauncherSession(Id,AccountId,AccessHash,AccessExpires,Expires,Revoked)
                VALUES('terminal',1000001,'terminal-access',0,0,1);
            """
        );
        saved.Database.ExecuteSql(
            $"""
            WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<{children})
                INSERT INTO LauncherRefresh(Hash,SessionId,Used) SELECT 'refresh-'||x,'terminal',1 FROM seq
            """
        );
    }

    [Fact]
    public void BatchesBoundChildDeletionAndRetainParentsUntilTheirChildrenAreGone()
    {
        using TestWorld world = new();
        LauncherStore store = new(new(world.Path), TimeProvider.System);
        store.Initialize();
        const int remainder = 5;
        SeedTerminalFamily(world.Path, LauncherStore.CleanupBatchSize + remainder);
        Assert.Equal(new LauncherCleanup(0, LauncherStore.CleanupBatchSize, 0), store.CleanupExpired());
        Assert.Equal(1, Count(world.Path, "LauncherSession"));
        Assert.Equal(new LauncherCleanup(0, remainder, 1), store.CleanupExpired());
        Assert.Equal(0, Count(world.Path, "LauncherRefresh"));
        Assert.Equal(new LauncherCleanup(0, 0, 0), store.CleanupExpired());
    }

    [Fact]
    public async Task PeriodicMaintenanceUsesTheClockAndStopsBeforeWorldDisposal()
    {
        TestClock time = new();
        CheckpointClock clock = new(time);
        using TestWorld world = new();
        LauncherStore store = new(new(world.Path), clock);
        store.Initialize();
        await using LauncherMaintenance maintenance = new(store, clock);
        maintenance.Start();
        SeedTerminalFamily(world.Path, 1);
        time.Seconds = (long)LauncherMaintenance.Interval.TotalSeconds - 1;
        clock.FireDueTimers();
        Assert.Equal(1, Count(world.Path, "LauncherSession"));
        time.Seconds++;
        clock.FireDueTimers();
        await maintenance.Pending;
        Assert.Equal(0, Count(world.Path, "LauncherSession"));
        await maintenance.DisposeAsync();
        SeedTerminalFamily(world.Path, 1);
        time.AdvanceWallTime(LauncherMaintenance.Interval);
        clock.FireDueTimers();
        Assert.Equal(1, Count(world.Path, "LauncherSession"));
        Assert.Throws<ObjectDisposedException>(maintenance.Start);
    }

    [Fact]
    public async Task FailedCheckpointIsReportedAndPendingCleanupBecomesDurableOnRetry()
    {
        using TestWorld world = new();
        TestClock time = new();
        CheckpointClock clock = new(time);
        bool fail = false;
        using LiveWorld live = new(
            new(world.Path),
            clock,
            _ => { },
            (snapshot, path) =>
            {
                if (fail)
                {
                    throw new IOException("Simulated cleanup checkpoint failure.");
                }
                LiveWorld.PersistSnapshot(snapshot, path);
            }
        );
        LauncherStore store = new(new(world.Path), clock);
        store.Initialize();
        SeedTerminalFamily(world.Path, 1);
        await live.FlushAsync();
        await using LauncherMaintenance maintenance = new(store, clock);
        maintenance.Start();
        fail = true;
        time.AdvanceWallTime(LauncherMaintenance.Interval);
        clock.FireDueTimers();
        await maintenance.Pending;
        Assert.IsType<IOException>(maintenance.LastFailure);
        Assert.Equal(1, Count(world.Path, "LauncherSession", disk: true));
        Assert.Equal(0, Count(world.Path, "LauncherSession"));
        fail = false;
        time.AdvanceWallTime(LauncherMaintenance.Interval);
        clock.FireDueTimers();
        await maintenance.Pending;
        await live.PendingSave;
        Assert.Null(maintenance.LastFailure);
        Assert.Null(live.LastSaveFailure);
        Assert.Equal(0, Count(world.Path, "LauncherSession", disk: true));
    }
}
