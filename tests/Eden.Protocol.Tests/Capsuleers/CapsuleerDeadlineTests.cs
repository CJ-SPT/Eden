using System.Threading.Channels;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerDeadlineTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 30)]
    public async Task InSpaceRetryUsesTheSavedDeadlineAndCheckpointsItsRemainingWait(bool background, int nextSecond)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        var founder = f.Founder();
        SqliteWorld ships = new(f.Database, f.Clock);
        var pilot = ships.FindPilot(-1, founder.PilotId)!;
        ships.SetPosition(pilot.ShipId, new(1_000_000, 0, 0), pilot.SystemId);
        HaulerProgress work = new(
            HaulerActivity.Waiting,
            0,
            30,
            pilot.ShipId,
            60003760,
            0,
            new NavigationState().Capture(),
            WaitingReason: "Retry boundary",
            DueTicks: TimeSpan.FromSeconds(30).Ticks
        );
        using (WorldDbContext world = new(f.Database.Path))
        {
            new HaulerCheckpoint(founder.PilotId, work).Apply(world);
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var job = scheduler.Jobs().Single(j => j.PilotId == founder.PilotId);
        var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(founder.PilotId);
        f.Clock.Seconds = 1;
        var next = hauler.Execute(job, 1, background);
        Assert.Equal(TimeSpan.FromSeconds(nextSecond).Ticks, next.DueTicks);
        Assert.Equal(HaulerActivity.Waiting, hauler.Read()!.Work.Activity);
        Assert.Equal(work.DueTicks, hauler.Read()!.Work.DueTicks);
        await host.GetRequiredService<LiveWorld>().FlushAsync();
        string checkpoint = Path.Combine(Path.GetDirectoryName(f.Database.Path)!, "deadline-checkpoint.sqlite");
        File.Copy(f.Database.Path, checkpoint);
        using WorldDbContext reopened = new(checkpoint);
        var saved = reopened.Set<SavedCapsuleerJob>().Single(j => j.PilotId == founder.PilotId);
        Assert.Equal(next.DueTicks, saved.DueTicks);
        Assert.Equal(next.Sequence, saved.Sequence);
        Assert.Equal(hauler.Read()!.Work, HaulerCheckpoint.Read(reopened.Set<SavedCapsuleerHauler>().Single().Progress));
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 30)]
    [InlineData(true, true, 1)]
    public void BackgroundRetriesSleepUntilTheirDeadlineWhileObservedOrDefendingPilotsKeepChecking(
        bool background,
        bool defending,
        int seconds
    )
    {
        HaulerProgress work = new(
            HaulerActivity.Waiting,
            0,
            30,
            1,
            60015157,
            0,
            new NavigationState().Capture(),
            DueTicks: TimeSpan.FromSeconds(30).Ticks
        )
        {
            Defense = defending ? new CapsuleerDefenseState { ShipId = 1 } : null,
        };
        Assert.Equal(TimeSpan.FromSeconds(seconds).Ticks, CapsuleerScheduleRules.Next(work, true, background).DueTicks);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, CapsuleerScheduleRules.Next(work, false, background).DueTicks);
    }

    [Fact]
    public void QueueDeadlineRetainsSubsecondPrecisionAndStopsWhenPlaytimeIsPaused()
    {
        CapsuleerJobQueue queue = new();
        Assert.Null(queue.NextDueTicks);
        Assert.Null(CapsuleerScheduleRules.WakeDelay(null, 0, true));
        Assert.Null(CapsuleerScheduleRules.WakeDelay(10, 0, false));
        Assert.Equal(TimeSpan.Zero, CapsuleerScheduleRules.WakeDelay(10, 10, false));
        Assert.Equal(TimeSpan.Zero, CapsuleerScheduleRules.WakeDelay(10, 11, true));
        Assert.Equal(TimeSpan.FromTicks(1), CapsuleerScheduleRules.WakeDelay(10, 9, true));
        queue.Restore([new(1, 0, 0, 20, CapsuleerAction.Collect), new(2, 0, 0, 10, CapsuleerAction.Lock)]);
        Assert.Equal(10, queue.NextDueTicks);
        Assert.True(queue.Publish(new(2, 1, 0, 5, CapsuleerAction.Lock)));
        Assert.Equal(5, queue.NextDueTicks);
        queue.Remove(2);
        Assert.Equal(20, queue.NextDueTicks);
    }

    [Fact]
    public async Task MaintenanceWaitsForTheExactDeadlineAndRechecksAnEarlyTimer()
    {
        using Harness h = new();
        h.Scheduler.Due = TimeSpan.FromSeconds(60);
        h.Worker.Start();
        var first = await h.Clock.NextTimer();
        Assert.Equal(TimeSpan.FromSeconds(60), first.DueTime);
        Assert.Equal(0, h.Scheduler.Calls);
        h.Clock.Elapsed = TimeSpan.FromSeconds(59);
        first.Fire();
        var remaining = await h.Clock.NextTimer();
        Assert.Equal(TimeSpan.FromSeconds(1), remaining.DueTime);
        Assert.Equal(0, h.Scheduler.Completed);
        h.Clock.Elapsed = TimeSpan.FromSeconds(60);
        remaining.Fire();
        var next = await h.Clock.NextTimer();
        Assert.Equal(TimeSpan.FromSeconds(60), next.DueTime);
        Assert.Equal(1, h.Scheduler.Completed);
        Assert.Equal(2, h.Scheduler.Calls);
    }

    [Fact]
    public async Task LongSavedDeadlinesUseBoundedTimersWithoutAdvancingWork()
    {
        using Harness h = new();
        h.Scheduler.Due = TimeSpan.MaxValue;
        h.Worker.Start();
        var timer = await h.Clock.NextTimer();
        Assert.Equal(TimeSpan.FromMilliseconds(uint.MaxValue - 1), timer.DueTime);
        Assert.Equal(0, h.Scheduler.Completed);
        Assert.Equal(0, h.Scheduler.Calls);
    }

    [Fact]
    public async Task NearbyDeadlinesBatchWithinTheExistingMaintenanceResolution()
    {
        using Harness h = new();
        h.Scheduler.Due = TimeSpan.FromTicks(1);
        h.Worker.Start();
        var timer = await h.Clock.NextTimer();
        Assert.Equal(CapsuleerJobQueue.WakeInterval, timer.DueTime);
        Assert.Equal(0, h.Scheduler.Completed);
        h.Clock.Elapsed = CapsuleerJobQueue.WakeInterval;
        timer.Fire();
        _ = await h.Clock.NextTimer();
        Assert.Equal(1, h.Scheduler.Completed);
    }

    [Fact]
    public async Task InputChangesCancelLaterTimersAndAnEmptyScheduleWaitsWithoutPolling()
    {
        using Harness h = new();
        h.Worker.Start();
        await h.Scheduler.WaitForPlan(0);
        Assert.Equal(0, h.Clock.Created);
        h.Scheduler.Reschedule(TimeSpan.FromSeconds(60));
        var later = await h.Clock.NextTimer();
        Assert.Equal(TimeSpan.FromSeconds(60), later.DueTime);
        h.Scheduler.Reschedule(TimeSpan.FromSeconds(1));
        var earlier = await h.Clock.NextTimer();
        Assert.True(later.Disposed);
        Assert.Equal(TimeSpan.FromSeconds(1), earlier.DueTime);
        h.Scheduler.Reschedule(null);
        var paused = await h.Scheduler.WaitForPlan(3);
        Assert.Null(paused.Delay);
        Assert.True(earlier.Disposed);
        Assert.Equal(2, h.Clock.Created);
        Assert.Equal(0, h.Scheduler.Completed);
    }

    [Fact]
    public async Task ChangesDuringATurnAreNotLostAndFailuresRetainBoundedRetries()
    {
        using (Harness h = new())
        {
            h.Scheduler.Due = TimeSpan.Zero;
            h.Scheduler.ChangeDuringFirstTurn = true;
            h.Worker.Start();
            var timer = await h.Clock.NextTimer();
            Assert.Equal(2, h.Scheduler.Calls);
            Assert.Equal(1, h.Scheduler.Completed);
            Assert.Equal(TimeSpan.FromSeconds(60), timer.DueTime);
        }
        using (Harness h = new())
        {
            h.Scheduler.Due = TimeSpan.Zero;
            h.Scheduler.FailFirstTurn = true;
            h.Worker.Start();
            var retry = await h.Clock.NextTimer();
            Assert.Equal(CapsuleerJobQueue.WakeInterval, retry.DueTime);
            Assert.Equal(1, h.Scheduler.Calls);
            h.Clock.Elapsed = CapsuleerJobQueue.WakeInterval;
            retry.Fire();
            var next = await h.Clock.NextTimer();
            Assert.Equal(1, h.Scheduler.Completed);
            h.Worker.Dispose();
            Assert.True(next.Disposed);
        }
    }

    [Fact]
    public async Task CommittedAggressionWakesThePendingJobWithoutConsumingItsActionAndCheckpointsTheRevision()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var selected = host.GetRequiredService<IHumanPresence>().Select(90000001, 30000141);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var worker = host.GetRequiredService<CapsuleerMaintenance>();
        f.Clock.Seconds++;
        do
        {
            worker.Tick();
        } while (scheduler.NextWake().Delay == TimeSpan.Zero);
        var founder = f.Founder();
        var before = scheduler.Jobs().Single(j => j.PilotId == founder.PilotId);
        var progress = scheduler.Hauler(founder.PilotId)!.Work;
        var sleeping = scheduler.NextWake();
        Assert.True(sleeping.Delay > TimeSpan.Zero);
        host.GetRequiredService<ICapsuleerSimulation>().ObserveAggression(founder.FounderShipId);
        Assert.True(sleeping.Changed.IsCompletedSuccessfully);
        var woken = scheduler.Jobs().Single(j => j.PilotId == founder.PilotId);
        Assert.Equal(before.Sequence, woken.Sequence);
        Assert.Equal(before.Revision + 1, woken.Revision);
        Assert.Equal(progress, scheduler.Hauler(founder.PilotId)!.Work);
        Assert.Equal(TimeSpan.Zero, scheduler.NextWake().Delay);
        await host.GetRequiredService<LiveWorld>().FlushAsync();
        using (SqliteConnection disk = new($"Data Source={f.Database.Path};Mode=ReadOnly;Pooling=False"))
        {
            disk.Open();
            using var command = disk.CreateCommand();
            command.CommandText = "SELECT Revision, Sequence, DueTicks FROM WorldCapsuleerJob WHERE PilotId=$pilot";
            command.Parameters.AddWithValue("$pilot", founder.PilotId);
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal(woken.Revision, row.GetInt64(0));
            Assert.Equal(woken.Sequence, row.GetInt64(1));
            Assert.Equal(woken.DueTicks, row.GetInt64(2));
        }
        worker.Tick();
        Assert.Equal(before.Sequence + 1, scheduler.Jobs().Single(j => j.PilotId == founder.PilotId).Sequence);
        selected.Dispose();
        Assert.Null(scheduler.NextWake().Delay);
    }

    [Fact]
    public void WarmPlansUseNoSqlAndCommittedChangesWakeEvenWhenTheWorldIsPaused()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        _ = scheduler.NextWake();
        using (var metrics = SessionPerformance.Begin("warm capsuleer wake plans", _ => { }, force: true)!)
        {
            for (int i = 0; i < 100; i++)
            {
                Assert.Null(scheduler.NextWake().Delay);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        var before = scheduler.NextWake();
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Pilots.Where(p => p.Id == 90000001).ExecuteUpdate(setters => setters.SetProperty(p => p.Name, "Wake pilot"));
        }
        Assert.True(before.Changed.IsCompletedSuccessfully);
        Assert.Null(scheduler.NextWake().Delay);
        var selection = scheduler.NextWake();
        using var selected = host.GetRequiredService<IHumanPresence>().Select(90000001, 30000141);
        Assert.True(selection.Changed.IsCompletedSuccessfully);
        Assert.NotNull(scheduler.NextWake().Delay);
    }

    [Fact]
    public void ReadOnlySynchronizationDoesNotWakeAnUnchangedSchedule()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var selected = host.GetRequiredService<IHumanPresence>().Select(90000001, 30000141);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var before = scheduler.NextWake();
        host.GetRequiredService<ICapsuleerHandoff>().Synchronize(30000141, () => { });
        Assert.False(before.Changed.IsCompleted);
        Assert.Same(before.Changed, scheduler.NextWake().Changed);
    }

    private sealed class Harness : IDisposable
    {
        private readonly CapsuleerHaulerTests.Fixture _fixture = new();
        private readonly ServiceProvider _host;
        internal ControlledClock Clock { get; } = new();
        internal DeadlineScheduler Scheduler { get; }
        internal CapsuleerMaintenance Worker { get; }

        internal Harness()
        {
            _host = ServerServices.Build(_fixture.Database, _fixture.Clock, liveWorld: true);
            Scheduler = new(Clock);
            Worker = new(
                Scheduler,
                _host.GetRequiredService<ICapsuleerHandoff>(),
                _host.GetRequiredService<ICapsuleerScene>(),
                _host.GetRequiredService<ChatRuntime>(),
                Clock,
                _host.GetRequiredService<IWorldSynchronization>(),
                _host.GetRequiredService<ICapsuleerUpdateDiagnostics>()
            );
        }

        public void Dispose()
        {
            Worker.Dispose();
            _host.Dispose();
            _fixture.Dispose();
        }
    }

    private sealed class DeadlineScheduler(ControlledClock clock) : ICapsuleerScheduler
    {
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<(int Calls, CapsuleerWakePlan Plan)> _plans = Channel.CreateUnbounded<(int, CapsuleerWakePlan)>();
        internal TimeSpan? Due { get; set; }
        internal bool FailFirstTurn { get; set; }
        internal bool ChangeDuringFirstTurn { get; set; }
        internal int Calls { get; private set; }
        internal int Completed { get; private set; }

        internal void Reschedule(TimeSpan? due)
        {
            Due = due;
            var old = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            old.TrySetResult();
        }

        public CapsuleerWakePlan NextWake()
        {
            CapsuleerWakePlan plan = new(CapsuleerScheduleRules.WakeDelay(Due?.Ticks, clock.Elapsed.Ticks, true), _changed.Task);
            _plans.Writer.TryWrite((Calls, plan));
            return plan;
        }

        internal async Task<CapsuleerWakePlan> WaitForPlan(int calls)
        {
            while (true)
            {
                var next = await _plans.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if (next.Calls >= calls)
                {
                    return next.Plan;
                }
            }
        }

        public CapsuleerBatch Run(int destinyStamp, int maximumActions = CapsuleerJobQueue.ActionsPerTurn)
        {
            Calls++;
            if (FailFirstTurn && Calls == 1)
            {
                throw new IOException("Transient job failure.");
            }
            bool completed = Due is { } due && due <= clock.Elapsed;
            if (completed)
            {
                Completed++;
                Due = clock.Elapsed + TimeSpan.FromSeconds(60);
            }
            if (ChangeDuringFirstTurn && Calls == 1)
            {
                Reschedule(Due);
            }
            return new(new(completed ? 1 : 0, 1, null, false), [], []);
        }

        public IReadOnlyList<CapsuleerJob> Jobs() => [];

        public MinerObservation? Miner(long pilotId) => null;

        public HaulerObservation? Hauler(long pilotId) => null;
    }

    private sealed class ControlledClock : TimeProvider
    {
        private readonly Channel<ControlledTimer> _timers = Channel.CreateUnbounded<ControlledTimer>();
        internal TimeSpan Elapsed { get; set; }
        internal int Created { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Elapsed.Ticks;

        internal Task<ControlledTimer> NextTimer() => _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            ControlledTimer timer = new(callback, state, dueTime);
            Created++;
            _timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        internal TimeSpan DueTime { get; private set; } = dueTime;
        internal bool Disposed { get; private set; }

        internal void Fire() => callback(state);

        public bool Change(TimeSpan due, TimeSpan period)
        {
            DueTime = due;
            return !Disposed;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
