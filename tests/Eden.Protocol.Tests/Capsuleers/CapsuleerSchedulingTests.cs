using System.Diagnostics;
using Eden.Gameplay.Capsuleers;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerSchedulingTests(ITestOutputHelper output)
{
    [Fact]
    public void DueJobsUseStableOrderRejectStaleRevisionsAndRetryFailedActions()
    {
        var clock = new TestClock();
        CapsuleerJobQueue queue = new();
        queue.Restore([new(3, 0, 0, 10, CapsuleerAction.Trade), new(1, 0, 0, 10, CapsuleerAction.Prepare)]);
        Assert.True(queue.Publish(new(3, 1, 1, 9, CapsuleerAction.Retry)));
        Assert.False(queue.Publish(new(3, 0, 0, 0, CapsuleerAction.Trade)));
        List<long> pilots = [];
        Assert.Throws<IOException>(() => queue.Run(10, _ => throw new IOException("uncommitted action"), clock));
        var turn = queue.Run(
            10,
            job =>
            {
                pilots.Add(job.PilotId);
                return job.Next(20, CapsuleerAction.Arrival);
            },
            clock
        );
        Assert.Equal(new long[] { 3, 1 }, pilots);
        Assert.Equal(2, turn.Completed);
        Assert.Null(turn.OldestDueTicks);
        Assert.Equal(0, queue.Run(19, _ => throw new InvalidOperationException(), clock).Completed);
        var next = queue.Run(20, job => job.Next(30, CapsuleerAction.Lock), clock);
        Assert.Equal(2, next.Completed);
        Assert.Equal(2, next.Pending);
    }

    [Theory]
    [InlineData(1804)]
    [InlineData(10000)]
    public void LargePopulationsYieldAtActionLimitAndOnlyExecuteDueJobs(int population)
    {
        var clock = new TestClock();
        CapsuleerJobQueue queue = new();
        long started = Stopwatch.GetTimestamp();
        queue.Restore(Enumerable.Range(1, population).Select(id => new CapsuleerJob(id, 0, 0, 100, CapsuleerAction.Prepare)));
        List<long> pilots = [];
        var turn = queue.Run(
            100,
            job =>
            {
                pilots.Add(job.PilotId);
                return job.Next(200, CapsuleerAction.Arrival);
            },
            clock
        );
        Assert.Equal(Enumerable.Range(1, CapsuleerJobQueue.ActionsPerTurn).Select(id => (long)id), pilots);
        Assert.Equal(CapsuleerJobQueue.ActionsPerTurn, turn.Completed);
        Assert.Equal(100L, turn.OldestDueTicks);
        Assert.Equal(population, turn.Pending);
        Assert.Equal(0, queue.Run(99, _ => throw new InvalidOperationException(), clock).Completed);
        output.WriteLine(
            $"Queue restore and first bounded turn for {population} agents: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms."
        );
    }

    [Fact]
    public void AtomicOverrunFinishesCurrentActionAndYieldsBeforeTheNext()
    {
        var clock = new TestClock();
        CapsuleerJobQueue queue = new();
        queue.Restore([new(1, 0, 0, 0, CapsuleerAction.Prepare), new(2, 0, 0, 0, CapsuleerAction.Prepare)]);
        var turn = queue.Run(
            0,
            job =>
            {
                clock.Milliseconds += 11;
                return job.Next(100, CapsuleerAction.Arrival);
            },
            clock
        );
        Assert.Equal(1, turn.Completed);
        Assert.True(turn.BudgetExceeded);
        Assert.Equal(0L, turn.OldestDueTicks);
        Assert.Equal(1, queue.Run(0, job => job.Next(100, CapsuleerAction.Arrival), clock).Completed);
    }

    [Fact]
    public void ReschedulingDoesNotExecuteStaleCallbacksAndInvalidRestorePreservesQueue()
    {
        var clock = new TestClock();
        CapsuleerJobQueue queue = new();
        queue.Restore([new(1, 0, 0, 1, CapsuleerAction.Prepare)]);
        for (int revision = 1; revision <= 10000; revision++)
        {
            Assert.True(queue.Publish(new(1, revision, revision, revision + 1, CapsuleerAction.Retry)));
        }
        Assert.Throws<ArgumentException>(() =>
            queue.Restore([new(2, 0, 0, 0, CapsuleerAction.Prepare), new(2, 0, 0, 0, CapsuleerAction.Prepare)])
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Publish(new(3, 0, 0, -1, CapsuleerAction.Prepare)));
        Assert.Equal(0, queue.Run(10000, _ => throw new InvalidOperationException(), clock).Completed);
        CapsuleerJob? executed = null;
        var turn = queue.Run(
            10001,
            job =>
            {
                executed = job;
                return job.Next(10002, CapsuleerAction.Arrival);
            },
            clock
        );
        Assert.Equal(10000, executed!.Revision);
        Assert.Equal(1, turn.Completed);
    }

    [Fact]
    public void HumanClockCountsOverlappingSessionsOnceAndPreservesFractionalTime()
    {
        const long frequency = 30000000;
        Guid first = Guid.NewGuid(),
            second = Guid.NewGuid();
        HumanActivity activity = new(123, 0);
        activity.Update(first, 1, 0, frequency);
        activity.Update(second, 1, 0, frequency);
        for (int stamp = 1; stamp <= 30; stamp++)
        {
            activity.Read(stamp, frequency);
        }
        var sameSystem = activity.Read(30, frequency);
        Assert.Equal(133, sameSystem.ElapsedTicks);
        Assert.Equal(2, sameSystem.SelectedSessions);
        Assert.Equal(new[] { 1 }, sameSystem.ActiveSystems);
        activity.Update(first, 2, frequency, frequency);
        var transferred = activity.Read(frequency, frequency);
        Assert.Equal(new[] { 1, 2 }, transferred.ActiveSystems);
        Assert.Equal(new[] { 1 }, sameSystem.ActiveSystems);
        activity.Update(first, null, frequency, frequency);
        activity.Update(second, null, frequency * 2, frequency);
        Assert.Equal(123 + TimeSpan.TicksPerSecond * 2, activity.Read(frequency * 100, frequency).ElapsedTicks);
        Assert.False(activity.Read(frequency * 100, frequency).Active);
        activity.Update(first, 2, frequency * 100, frequency);
        Assert.Equal(123 + TimeSpan.TicksPerSecond * 3, activity.Read(frequency * 101, frequency).ElapsedTicks);
    }

    [Fact]
    public void InvalidClockUpdatesPreserveMembershipAndElapsedTime()
    {
        Guid session = Guid.NewGuid();
        HumanActivity activity = new(0, 0);
        activity.Update(session, 1, 0, 1000);
        Assert.Throws<ArgumentOutOfRangeException>(() => activity.Update(session, 0, 1000, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => activity.Read(-1, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => activity.Read(1000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => activity.Read(1000, 2000));
        var current = activity.Read(1000, 1000);
        Assert.Equal(TimeSpan.TicksPerSecond, current.ElapsedTicks);
        Assert.Equal(new[] { 1 }, current.ActiveSystems);
    }
}
