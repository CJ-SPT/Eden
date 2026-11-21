using System.Diagnostics;
using Eden.Gameplay.Capsuleers;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerSchedulingTests(ITestOutputHelper output)
{
    [Fact]
    public void FailedSafetyCheckRetriesBeforeThePaidDeadline()
    {
        long second = TimeSpan.TicksPerSecond;
        CapsuleerJobQueue queue = new();
        CapsuleerJob paid = new(1, 0, 0, 30 * second, CapsuleerAction.Collect);
        queue.Publish(paid);
        queue.ScheduleCheck(paid, second);
        Assert.True(queue.Defer(paid, second));
        queue.Restore([paid]);
        Assert.Equal(2 * second, queue.NextDueTicks);
        var result = queue.Run(
            2 * second,
            _ => throw new InvalidOperationException("Ore cannot settle before its paid deadline."),
            TimeProvider.System,
            check: (job, at) =>
            {
                Assert.Equal(second, at);
                queue.ScheduleCheck(job, 3 * second);
            }
        );
        Assert.Equal((0, 1), (result.Completed, result.SafetyChecks));
        Assert.Equal(3 * second, queue.NextDueTicks);
    }

    [Fact]
    public void AQuietCheckReleasesTheNextTradeForItsOwnPreparation()
    {
        CapsuleerJobQueue queue = new();
        CapsuleerJob miner = new(1, 1, 1, 20, CapsuleerAction.Collect);
        CapsuleerJob trader = new(2, 1, 1, 10, CapsuleerAction.Trade);
        queue.Restore([miner, trader]);
        queue.ScheduleCheck(miner, 9);
        var turn = queue.Run(
            10,
            _ => throw new InvalidOperationException("The trade has not prepared its inputs."),
            TimeProvider.System,
            useCanonicalQueue: true,
            maximumActions: 1,
            check: (job, _) => queue.ScheduleCheck(job, null)
        );
        Assert.Equal((0, 1), (turn.Completed, turn.SafetyChecks));
        Assert.Equal(trader, queue.NextJob);
    }

    [Fact]
    public void BoundedBackgroundSliceLeavesTheFirstUnpreparedDeadlineAtTheHead()
    {
        CapsuleerJobQueue queue = new();
        var clock = new TestClock();
        CapsuleerJob first = new(1, 1, 1, 9, CapsuleerAction.Arrival);
        CapsuleerJob second = new(2, 1, 1, 9, CapsuleerAction.Arrival);
        CapsuleerJob trade = new(3, 1, 1, 9, CapsuleerAction.Trade);
        CapsuleerJob later = new(4, 1, 1, 9, CapsuleerAction.Arrival);
        queue.Restore([later, trade, second, first]);
        List<CapsuleerJob> paid = [];

        var turn = queue.Run(9, Execute, clock, maximumActions: 4, mayExecute: job => job.Action == CapsuleerAction.Arrival);
        Assert.Equal(2, turn.Completed);
        Assert.Equal(new[] { first, second }, paid);
        Assert.Equal(trade, queue.NextJob);
        Assert.Equal(9, turn.OldestDueTicks);

        Assert.Equal(2, queue.Run(9, Execute, clock).Completed);
        Assert.Equal(new[] { first, second, trade, later }, paid);

        CapsuleerJob Execute(CapsuleerJob job)
        {
            paid.Add(job);
            return job.Next(10, job.Action);
        }
    }

    [Fact]
    public void SleepingPaidCycleChecksCanWakeAtTheSameDeadlineAsAnotherPilot()
    {
        CapsuleerJobQueue queue = new();
        CapsuleerJob paid = new(2, 1, 1, 12, CapsuleerAction.Collect);
        CapsuleerJob freight = new(1, 1, 1, 9, CapsuleerAction.Trade);
        queue.Publish(paid);
        queue.Publish(freight);
        queue.ScheduleCheck(paid, 6);
        List<long> settled = [];
        var first = queue.Run(6, Execute, TimeProvider.System, check: (job, at) => queue.ScheduleCheck(job, at + 3));
        Assert.Equal(0, first.Completed);
        Assert.Equal(9, queue.NextDueTicks);
        queue.Restore([paid, freight]);
        // A warning at tick nine becomes a real job and retains the canonical tie break.
        queue.Run(9, Execute, TimeProvider.System, check: Wake);
        while (queue.NextDueTicks <= 9)
        {
            queue.Run(9, Execute, TimeProvider.System, check: Wake);
        }
        Assert.Equal([1L, 2L], settled);

        CapsuleerJob Execute(CapsuleerJob job)
        {
            settled.Add(job.PilotId);
            return job.Next(20, job.Action);
        }
        void Wake(CapsuleerJob job, long at) => queue.Publish(job with { DueTicks = at, Revision = job.Revision + 1 });
    }

    [Fact]
    public void CanonicalQueuePreservesOrderingAndWithdrawsCompletedTravelersWithoutRebuildingCandidates()
    {
        const int Population = 1804;
        long Measure(bool canonical)
        {
            CapsuleerJobQueue queue = new();
            queue.Restore(Enumerable.Range(1, Population).Select(id => new CapsuleerJob(id, 0, 0, 0, CapsuleerAction.Arrival)));
            HashSet<long> running = Enumerable.Range(1, Population).Select(id => (long)id).ToHashSet();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int turn = 0; turn < 20; turn++)
            {
                var result = queue.Run(0, job => job.Next(100, CapsuleerAction.Arrival), new TestClock(), running, canonical);
                Assert.Equal(CapsuleerJobQueue.ActionsPerTurn, result.Completed);
            }
            return GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
        _ = Measure(true);
        long scoped = Measure(false);
        long canonical = Measure(true);
        output.WriteLine($"Due queue allocations scoped/canonical: {scoped}/{canonical} bytes.");
        Assert.True(canonical < scoped / 2);
        CapsuleerJobQueue queue = new();
        queue.Restore([new(1, 0, 0, 0, CapsuleerAction.GateTransfer), new(2, 0, 0, 0, CapsuleerAction.Arrival)]);
        HashSet<long> pilots = [1, 2];
        Assert.Throws<IOException>(() => queue.Run(0, _ => throw new IOException(), new TestClock(), pilots, true));
        List<long> order = [];
        queue.Run(
            0,
            job =>
            {
                order.Add(job.PilotId);
                pilots.Remove(job.PilotId);
                return job.Next(1, CapsuleerAction.Arrival);
            },
            new TestClock(),
            pilots,
            true
        );
        Assert.Equal(new long[] { 1, 2 }, order);
        Assert.Equal(0, queue.Run(1, _ => throw new InvalidOperationException(), new TestClock()).Completed);
        Assert.True(queue.Publish(new(1, 1, 1, 1, CapsuleerAction.Arrival)));
        Assert.Equal(1, queue.Run(1, job => job.Next(2, CapsuleerAction.Arrival), new TestClock()).Completed);
    }

    [Fact]
    public void RoleDeadlinesPreserveRemainingWorkAndDockedRetriesSleepUntilDue()
    {
        long elapsed = TimeSpan.FromMilliseconds(12345).Ticks;
        long deadline = CapsuleerScheduleRules.MigrateDeadline(elapsed, 100, 115);
        Assert.Equal(elapsed + TimeSpan.FromSeconds(15).Ticks, deadline);
        Assert.Equal(101, CapsuleerScheduleRules.Stamp(elapsed + TimeSpan.TicksPerSecond, elapsed - 100 * TimeSpan.TicksPerSecond));
        MinerProgress waiting = new(
            MinerActivity.Waiting,
            0,
            100,
            115,
            0,
            0,
            0,
            new Eden.Gameplay.Flight.NavigationState().Capture(),
            ActiveTicks: elapsed,
            DueTicks: deadline
        );
        Assert.Equal(new CapsuleerSchedule(deadline, CapsuleerAction.Retry), CapsuleerScheduleRules.Next(waiting, false));
        Assert.Equal(elapsed + TimeSpan.TicksPerSecond, CapsuleerScheduleRules.Next(waiting, true).DueTicks);
        var collecting = waiting with { Activity = MinerActivity.Mining, DueTicks = elapsed + TimeSpan.TicksPerMillisecond };
        Assert.Equal(collecting.DueTicks, CapsuleerScheduleRules.Next(collecting, true).DueTicks);
    }

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
    public void IdleLargePopulationKeepsAllocationBoundedAndWithdrawnJobsResumeOnce()
    {
        const int population = 10000;
        const int wakes = 100;
        const long allocationBudget = 16 * 1024;
        CapsuleerJobQueue queue = new();
        var jobs = Enumerable.Range(1, population).Select(id => new CapsuleerJob(id, 0, 0, 100, CapsuleerAction.Prepare)).ToArray();
        var pilots = jobs.Select(job => job.PilotId).ToHashSet();
        queue.Restore(jobs);
        TestClock clock = new();
        Func<CapsuleerJob, CapsuleerJob> unexpected = _ => throw new InvalidOperationException("No job is due.");
        queue.Run(99, unexpected, clock, pilots);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < wakes; i++)
        {
            queue.Run(99, unexpected, clock, pilots);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.InRange(allocated, 0, allocationBudget);
        Assert.True(queue.Remove(1));
        Assert.False(queue.Remove(1));
        Assert.Equal(population - 1, queue.Inspect(100).Due);
        Assert.True(queue.Publish(jobs[0]));
        List<long> executed = [];
        var turn = queue.Run(
            100,
            job =>
            {
                executed.Add(job.PilotId);
                return job.Next(200, CapsuleerAction.Arrival);
            },
            clock,
            pilots
        );
        Assert.Equal(Enumerable.Range(1, CapsuleerJobQueue.ActionsPerTurn).Select(id => (long)id), executed);
        Assert.Equal(population, turn.Pending);
        output.WriteLine($"{population} selected jobs, {wakes} idle wakes: {allocated} bytes.");
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
