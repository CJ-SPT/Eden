using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerLockOrderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginViewsWaitingForWorldOwnerDoNotHoldTheMovementGate(bool anomalies)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        using ManualResetEventSlim started = new();
        Thread? reader = null;
        Task request;
        bool enteredMovement;
        bool reachedWait;
        using (live.Enter())
        {
            request = Task.Factory.StartNew(
                () =>
                {
                    reader = Thread.CurrentThread;
                    started.Set();
                    if (anomalies)
                    {
                        host.GetRequiredService<ICombatAnomalies>().Directory(f.Clock.Now);
                    }
                    else
                    {
                        host.GetRequiredService<INpcEncounters>().List(30000141, f.Clock.Now);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            reachedWait = SpinWait.SpinUntil(
                () => reader!.ThreadState.HasFlag(ThreadState.WaitSleepJoin) || request.IsCompleted,
                TimeSpan.FromSeconds(5)
            );
            enteredMovement = Monitor.TryEnter(f.Database.MotionLock);
            if (enteredMovement)
            {
                Monitor.Exit(f.Database.MotionLock);
            }
        }
        // Release the owner before awaiting, including when testing the old failure.
        // The regression therefore cleans up rather than leaving deadlocked threads.
        await request.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(reachedWait);
        Assert.True(enteredMovement, "A login view held the movement gate while waiting for the resident owner.");
    }
}
