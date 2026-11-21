using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerFailureTests
{
    [Fact]
    public void FailedPilotBacksOffWithoutBlockingHealthyJobsOrChangingItsSavedDeadline()
    {
        CapsuleerJob failed = new(1, 0, 0, 0, CapsuleerAction.Prepare);
        CapsuleerJob healthy = new(2, 0, 0, 0, CapsuleerAction.Trade);
        CapsuleerJobQueue queue = new();
        queue.Restore([failed, healthy]);
        TestClock clock = new();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            long now = queue.NextDueTicks!.Value;
            Assert.Throws<IOException>(() => queue.Run(now, _ => throw new IOException("Broken pilot"), clock, maximumActions: 1));
            Assert.True(queue.Defer(failed, now));
            // The scheduler rebuilds from authoritative jobs after any failed role.
            queue.Restore([failed, healthy]);
            Assert.Equal(healthy, queue.NextJob);
            var turn = queue.Run(now, job => job.Next(now + TimeSpan.FromMinutes(1).Ticks, job.Action), clock, maximumActions: 1);
            Assert.Equal(1, turn.Completed);
            healthy = healthy.Next(now + TimeSpan.FromMinutes(1).Ticks, healthy.Action);
            long retry = queue.NextDueTicks!.Value;
            long expectedDelay = Math.Min(1L << Math.Min(attempt, CapsuleerRetry.MaximumAttempts - 1), 30);
            Assert.Equal(TimeSpan.FromSeconds(expectedDelay).Ticks, retry - now);
            Assert.Equal(failed, queue.NextJob);
            Assert.Equal(0, queue.Run(retry - 1, _ => throw new Exception("Too early"), clock).Completed);
            // Put a healthy action next to the broken pilot for the next retry.
            healthy = healthy.Next(retry, healthy.Action);
            Assert.True(queue.Publish(healthy));
        }
    }

    [Fact]
    public void SuccessNewRevisionsAndWithdrawalResetBackoffAndStaleFailuresCannotDelayNewWork()
    {
        CapsuleerJob job = new(1, 0, 0, 0, CapsuleerAction.Collect);
        CapsuleerJobQueue queue = new();
        queue.Restore([job]);
        Assert.True(queue.Defer(job, 0));
        Assert.True(queue.Defer(job, 0));
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks, queue.NextDueTicks);
        queue.Run(TimeSpan.FromSeconds(2).Ticks, current => current.Next(1, current.Action), new TestClock(), maximumActions: 1);
        var next = job.Next(1, job.Action);
        Assert.False(queue.Defer(job, 0));
        Assert.True(queue.Defer(next, 0));
        Assert.Equal(CapsuleerRetry.InitialDelay.Ticks, queue.NextDueTicks);
        Assert.True(queue.Remove(next.PilotId));
        Assert.True(queue.Publish(next));
        Assert.Equal(next.DueTicks, queue.NextDueTicks);
        Assert.True(queue.Defer(next, 0));
        var replacement = next.Next(2, next.Action);
        Assert.True(queue.Publish(replacement));
        Assert.Equal(replacement.DueTicks, queue.NextDueTicks);
        Assert.False(queue.Defer(next, 0));
    }

    [Fact]
    public void RetryDeadlineSaturatesWithoutOverflowAndRejectsNegativeTime()
    {
        Assert.Equal(long.MaxValue, CapsuleerRetry.Next(null, long.MaxValue).DueTicks);
        Assert.Throws<ArgumentOutOfRangeException>(() => CapsuleerRetry.Next(null, -1));
    }

    [Fact]
    public async Task PostCommitObservationFailureDoesNotReplayTheActionAndHealthyPilotStillRuns()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, 30000141);
        FailingHealth health = new(host.GetRequiredService<IPilotHealth>());
        var miners = ActivatorUtilities.CreateInstance<CapsuleerMinerFactory>(host, health);
        var haulers = ActivatorUtilities.CreateInstance<CapsuleerHaulerFactory>(host, health);
        var scheduler = ActivatorUtilities.CreateInstance<CapsuleerScheduler>(host, miners, haulers);
        scheduler.Run(0);
        var original = scheduler.Jobs().First();
        health.PilotId = original.PilotId;
        health.Fail = true;
        fixture.Clock.Seconds++;
        Assert.Throws<IOException>(() => scheduler.Run(1, maximumActions: 1));
        var committed = scheduler.Jobs().Single(job => job.PilotId == original.PilotId);
        Assert.Equal(original.Revision + 1, committed.Revision);
        Assert.Equal(1, scheduler.Read().Failures);
        Assert.Contains(original.PilotId.ToString(), scheduler.Read().LastFailure!);
        Assert.Equal(1, scheduler.Run(1, maximumActions: 1).Turn.Completed);
        Assert.Equal(committed, scheduler.Jobs().Single(job => job.PilotId == original.PilotId));
        health.Fail = false;
        fixture.Clock.Milliseconds += 999;
        Assert.Equal(0, scheduler.Run(1, maximumActions: 1).Turn.Completed);
        fixture.Clock.Milliseconds++;
        Assert.Equal(1, scheduler.Run(2, maximumActions: 1).Turn.Completed);
        Assert.Equal(committed.Revision + 1, scheduler.Jobs().Single(job => job.PilotId == original.PilotId).Revision);
        Assert.Equal(1, host.GetRequiredService<LiveWorld>().MinerCheckpoints[original.PilotId].Work.Trips);
        human.Dispose();
        await host.GetRequiredService<LiveWorld>().FlushAsync();
        var saved = scheduler.Jobs().Single(job => job.PilotId == original.PilotId);
        host.Dispose();
        fixture.Clock.Seconds += 60;
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var returned = reopened.GetRequiredService<IHumanPresence>().Select(90000001, 30000141);
        Assert.Equal(saved, reopened.GetRequiredService<ICapsuleerScheduler>().Jobs().Single(job => job.PilotId == original.PilotId));
    }

    [Fact]
    public void PreCommitFailureRetainsTheCanonicalJobAndPaidProgressUntilRecovery()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, 30000141);
        FailingSimulation encounters = new(host.GetRequiredService<INpcSimulation>());
        var miners = ActivatorUtilities.CreateInstance<CapsuleerMinerFactory>(host, encounters);
        var scheduler = ActivatorUtilities.CreateInstance<CapsuleerScheduler>(host, miners);
        scheduler.Run(0);
        fixture.Clock.Seconds++;
        scheduler.Run(1);
        var live = host.GetRequiredService<LiveWorld>();
        long pilot = live.MinerCheckpoints.Keys.Single();
        var before = live.MinerCheckpoints[pilot];
        var job = live.CapsuleerJobs[pilot];
        encounters.Fail = true;
        fixture.Clock.Seconds++;
        Assert.Throws<IOException>(() => scheduler.Run(2, maximumActions: 1));
        Assert.Equal(job, live.CapsuleerJobs[pilot]);
        Assert.Equal(before, live.MinerCheckpoints[pilot]);
        Assert.Equal(1, scheduler.Run(2, maximumActions: 1).Turn.Completed);
        Assert.Equal(job, live.CapsuleerJobs[pilot]);
        encounters.Fail = false;
        fixture.Clock.Seconds++;
        Assert.Equal(1, scheduler.Run(3, maximumActions: 1).Turn.Completed);
        Assert.Equal(job.Revision + 1, live.CapsuleerJobs[pilot].Revision);
        Assert.Equal(before.Work.Trips, live.MinerCheckpoints[pilot].Work.Trips);
    }

    private sealed class FailingSimulation(INpcSimulation inner) : INpcSimulation
    {
        internal bool Fail { get; set; }

        public IReadOnlyList<NpcSimulationContact> Advance(int systemId, long now)
        {
            if (Fail)
            {
                throw new IOException("NPC inputs temporarily unavailable.");
            }
            return inner.Advance(systemId, now);
        }
    }

    private sealed class FailingHealth(IPilotHealth inner) : IPilotHealth
    {
        internal long PilotId { get; set; }
        internal bool Fail { get; set; }

        public ShipHealth Read(long pilotId, long now)
        {
            if (Fail && pilotId == PilotId)
            {
                throw new IOException("Observation temporarily unavailable.");
            }
            return inner.Read(pilotId, now);
        }

        public bool WasDestroyed(long shipId) => inner.WasDestroyed(shipId);
    }
}
