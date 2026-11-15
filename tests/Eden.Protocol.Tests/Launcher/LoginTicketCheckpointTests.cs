using System.Collections.Concurrent;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Persistence;
using Eden.Server.Launcher;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Launcher;

public sealed class LoginTicketCheckpointTests
{
    private const string Password = "checkpoint ticket password";

    [Fact]
    public async Task ConcurrentTicketsDoNotWaitForAnUnrelatedCheckpoint()
    {
        using TestWorld world = new();
        CheckpointClock clock = new(new TestClock());
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        bool pause = false;
        using LiveWorld live = new(
            new(world.Path),
            clock,
            _ => { },
            (snapshot, path) =>
            {
                if (pause)
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }
                LiveWorld.PersistSnapshot(snapshot, path);
            }
        );
        LauncherStore store = new(new(world.Path), clock);
        store.Initialize();
        var account = store.Register(new("Concurrent tickets", Password));
        var session = store.SignIn(new("Concurrent tickets", Password, true));
        string initial = await store.IssueTicketAsync(session.AccessToken);
        live.ObjectsChanged();
        pause = true;
        Task saving = live.FlushAsync();
        Task<long?[]>? logins = null;
        ConcurrentBag<PerformanceSample> samples = [];
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            logins = Task.WhenAll(
                Enumerable
                    .Range(0, 4)
                    .Select(_ =>
                        Task.Run(async () =>
                        {
                            using var metrics = SessionPerformance.Capture("ticket", samples.Add);
                            string ticket = await store.IssueTicketAsync(session.AccessToken);
                            return await store.ConsumeTicketAsync(ticket);
                        })
                    )
            );
            Assert.All(await logins.WaitAsync(TimeSpan.FromSeconds(2)), id => Assert.Equal(account.AccountId, id));
            Assert.Equal(account.AccountId, await store.ConsumeTicketAsync(initial).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Null(await store.ConsumeTicketAsync(initial));
            Assert.False(saving.IsCompleted);
            Assert.Equal(4, samples.Count);
            Assert.All(
                samples,
                sample =>
                {
                    Assert.Equal(0, sample.Commands);
                    Assert.Equal(0, sample.Connections);
                    Assert.Equal(0, sample.Fittings);
                }
            );
        }
        finally
        {
            pause = false;
            release.Set();
            await saving;
            if (logins is not null)
            {
                await logins;
            }
        }
        await live.FlushAsync();
        Assert.Null(live.LastSaveFailure);
    }

    [Fact]
    public async Task RestoringACheckpointCannotReplayConsumedOrUnusedTicketsButKeepsRememberedSignIn()
    {
        using TestWorld world = new();
        CheckpointClock clock = new(new TestClock());
        var database = new WorldDatabase(world.Path);
        using LiveWorld live = new(database, clock, _ => { });
        LauncherStore store = new(database, clock);
        store.Initialize();
        var account = store.Register(new("Restart tickets", Password));
        var session = store.SignIn(new("Restart tickets", Password, true));
        string used = await store.IssueTicketAsync(session.AccessToken);
        string unused = await store.IssueTicketAsync(session.AccessToken);
        await live.FlushAsync();
        string restoredPath = Path.Combine(Path.GetDirectoryName(world.Path)!, "restored.sqlite");
        File.Copy(world.Path, restoredPath);
        Assert.Equal(account.AccountId, await store.ConsumeTicketAsync(used));

        var restoredDatabase = new WorldDatabase(restoredPath);
        using LiveWorld restored = new(restoredDatabase, clock, _ => { });
        LauncherStore reopened = new(restoredDatabase, clock);
        reopened.Initialize();
        Assert.Null(await reopened.ConsumeTicketAsync(used));
        Assert.Null(await reopened.ConsumeTicketAsync(unused));
        Assert.Null(await reopened.ConsumeTicketAsync("eden3.invalid"));
        var refreshed = reopened.Refresh(session.RefreshToken!);
        Assert.Equal(account.AccountId, refreshed.AccountId);
        string current = await reopened.IssueTicketAsync(refreshed.AccessToken);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => reopened.ConsumeTicketAsync(current))));
        Assert.Single(attempts, id => id == account.AccountId);
        Assert.Equal(3, attempts.Count(id => id is null));
        Assert.Null(await store.ConsumeTicketAsync(current));
        Assert.Equal(account.AccountId, await store.ConsumeTicketAsync(unused));
    }
}
