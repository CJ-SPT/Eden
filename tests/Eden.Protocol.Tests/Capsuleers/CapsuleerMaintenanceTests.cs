using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Configuration;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Docking;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerMaintenanceTests
{
    [Fact]
    public void HighDensityDockedGuestsReceiveOneSharedLocalRosterAndUnchangedFramesStayQuiet()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        WorldConfigurationStore configuration = new(f.Database, f.Clock);
        var state = configuration.Read();
        configuration.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with { Capsuleers = new(TrafficMultiplier: 10, GateVisitors: 6) },
            },
            "test"
        );
        WorldInitialization.Initialize(f.Database, f.Clock);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(30000141);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var chat = host.GetRequiredService<ChatRuntime>();
        Guid session = Guid.NewGuid();
        const long humanId = 90000001;
        const int system = 30000141;
        ChatRoom local = new(ChatRoomKind.Local, system);
        chat.Update(session, new(humanId, "Observer", 1000044, system, 0));
        var connection = chat.Authenticate(humanId.ToString(), chat.IssueToken(session));
        chat.Join(connection.Id, local, "joined");
        while (connection.Events.TryRead(out _)) { }
        using var presence = host.GetRequiredService<IHumanPresence>().Select(humanId, system);
        var worker = host.GetRequiredService<CapsuleerMaintenance>();
        worker.Refresh();
        var human = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        var guests = host.GetRequiredService<SharedStationGuests>().List(human);
        Assert.NotEmpty(guests);
        var members = chat.Members(connection.Id, local);
        Assert.All(guests, guest => Assert.Contains(members, member => member.Id == guest.PilotId));
        List<ChatDelivery> deliveries = [];
        while (connection.Events.TryRead(out var delivery))
        {
            deliveries.Add(delivery);
        }
        var roster = Assert.Single(deliveries.Select(d => d.Event).OfType<ChatEvent.Roster>());
        Assert.Equal(members.Select(p => p.Id), roster.Pilots.Select(p => p.Id));
        var population = host.GetRequiredService<ICapsuleerPopulation>();
        Assert.True(population.Summary().PreparedSystems > 0);
        Assert.True(host.GetRequiredService<ICapsuleerDiagnostics>().Read().ScheduledAgents > 0);
        Assert.True(Assert.Single(population.Systems(), value => value.SystemId == system).CurrentPolicy);
        using (var metrics = SessionPerformance.Begin("unchanged Local and population reads", _ => { }, force: true)!)
        {
            for (int i = 0; i < 20; i++)
            {
                worker.Refresh();
                _ = population.Summary();
                _ = population.Systems();
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.False(connection.Events.TryRead(out _));
    }

    [Fact]
    public async Task ObserverCanCheckpointBetweenTwoDueJobsInOneMaintenanceTurn()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var selected = host.GetRequiredService<IHumanPresence>().Select(90000001, 30000141);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var jobs = scheduler.Jobs().ToArray();
        Assert.Equal(2, jobs.Length);
        var ownership = new PauseAfterFirstJob(scheduler, host.GetRequiredService<IWorldSynchronization>());
        using CapsuleerMaintenance worker = new(
            ownership,
            host.GetRequiredService<ICapsuleerHandoff>(),
            host.GetRequiredService<ICapsuleerScene>(),
            host.GetRequiredService<ChatRuntime>(),
            f.Clock,
            host.GetRequiredService<IWorldSynchronization>(),
            host.GetRequiredService<ICapsuleerUpdateDiagnostics>()
        );
        f.Clock.Seconds++;
        var turn = Task.Run(worker.Tick);
        try
        {
            await ownership.BetweenJobs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var committed = scheduler.Jobs().ToDictionary(j => j.PilotId);
            Assert.Equal(jobs[0].Sequence + 1, committed[jobs[0].PilotId].Sequence);
            Assert.Equal(jobs[1], committed[jobs[1].PilotId]);
            await host.GetRequiredService<LiveWorld>().FlushAsync();
            using var disk = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = f.Database.Path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString()
            );
            disk.Open();
            using var command = disk.CreateCommand();
            command.CommandText = "SELECT PilotId, Sequence FROM WorldCapsuleerJob ORDER BY PilotId";
            using var rows = command.ExecuteReader();
            Assert.True(rows.Read());
            Assert.Equal(jobs[0].PilotId, rows.GetInt64(0));
            Assert.Equal(1, rows.GetInt64(1));
            Assert.True(rows.Read());
            Assert.Equal(jobs[1].PilotId, rows.GetInt64(0));
            Assert.Equal(0, rows.GetInt64(1));
            Assert.False(rows.Read());
        }
        finally
        {
            ownership.Resume.SetResult();
            await turn;
        }
        Assert.All(scheduler.Jobs(), job => Assert.Equal(1, job.Sequence));
    }

    private sealed class PauseAfterFirstJob(ICapsuleerScheduler scheduler, IWorldSynchronization actual) : ICapsuleerScheduler
    {
        public bool IsEntered => actual.IsEntered;
        public TaskCompletionSource BetweenJobs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _acquisitions;

        public IReadOnlyList<CapsuleerJob> Jobs() => scheduler.Jobs();

        public MinerObservation? Miner(long pilotId) => scheduler.Miner(pilotId);

        public HaulerObservation? Hauler(long pilotId) => scheduler.Hauler(pilotId);

        public CapsuleerBatch Run(int destinyStamp, int maximumActions = CapsuleerJobQueue.ActionsPerTurn)
        {
            if (++_acquisitions == 2)
            {
                Assert.False(IsEntered);
                BetweenJobs.SetResult();
                Resume.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            return scheduler.Run(destinyStamp, maximumActions);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteUpdateIncludesInitialReconciliationCaptureAndPresentationEvenOnFailure(bool failPresentation)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        CapsuleerScheduler scheduler = new(
            f.Database,
            host.GetRequiredService<CapsuleerActivitySource>(),
            host.GetRequiredService<ICapsuleerPopulation>(),
            host.GetRequiredService<IWorldSynchronization>(),
            f.Clock,
            host.GetRequiredService<CapsuleerMinerFactory>(),
            host.GetRequiredService<CapsuleerHaulerFactory>(),
            new TimedGateWatch(f.Clock),
            host.GetRequiredService<MarketChangeSignal>(),
            host.GetRequiredService<PilotMarketStore>()
        );
        using CapsuleerMaintenance maintenance = new(
            scheduler,
            new TimedHandoff(scheduler, f.Clock),
            new TimedScene(host.GetRequiredService<ICapsuleerScene>(), f.Clock, failPresentation),
            host.GetRequiredService<ChatRuntime>(),
            f.Clock,
            host.GetRequiredService<IWorldSynchronization>(),
            scheduler
        );
        if (failPresentation)
        {
            Assert.Throws<InvalidOperationException>(() => maintenance.Tick());
        }
        else
        {
            Assert.Null(maintenance.Tick());
        }
        var timing = scheduler.Read();
        Assert.Equal(TimeSpan.FromMilliseconds(11), timing.LastTurn);
        Assert.Equal(TimeSpan.FromMilliseconds(10), timing.LastPresentation);
        Assert.Equal(TimeSpan.FromMilliseconds(21), timing.LastUpdate);
        scheduler.Run(0);
        Assert.Equal(timing.LastUpdate, scheduler.Read().LastUpdate);
        Assert.Equal(timing.LastPresentation, scheduler.Read().LastPresentation);
    }

    [Fact]
    public async Task OneWakeDrainsMultipleBoundedTurnsWithoutWaitingForAnotherTimerTick()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        ManualWakeClock clock = new();
        BackloggedScheduler scheduler = new(clock);
        using CapsuleerMaintenance maintenance = new(
            scheduler,
            host.GetRequiredService<ICapsuleerHandoff>(),
            host.GetRequiredService<ICapsuleerScene>(),
            host.GetRequiredService<ChatRuntime>(),
            clock,
            host.GetRequiredService<IWorldSynchronization>(),
            host.GetRequiredService<ICapsuleerUpdateDiagnostics>()
        );
        maintenance.Start();
        clock.Wake();
        await scheduler.Current.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, scheduler.Calls);
    }

    private sealed class BackloggedScheduler(ManualWakeClock clock) : ICapsuleerScheduler
    {
        public TaskCompletionSource Current { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public CapsuleerBatch Run(int destinyStamp, int maximumActions = CapsuleerJobQueue.ActionsPerTurn)
        {
            Calls++;
            clock.Elapsed += CapsuleerJobQueue.ExecutionBudget;
            if (Calls == 3)
            {
                Current.SetResult();
            }
            return new(new(1, 200, Calls < 3 ? 1 : null, true), [], []);
        }

        public IReadOnlyList<CapsuleerJob> Jobs() => [];

        public MinerObservation? Miner(long pilotId) => null;

        public HaulerObservation? Hauler(long pilotId) => null;
    }

    private sealed class TimedGateWatch(TestClock clock) : ICapsuleerGateWatch
    {
        private bool _prepared;

        public IReadOnlyList<WatchedTrafficGate> Read(HumanActivitySnapshot activity)
        {
            if (!_prepared)
            {
                clock.Milliseconds += 11;
                _prepared = true;
            }
            return [];
        }
    }

    private sealed class TimedHandoff(ICapsuleerHandoff actual, TestClock clock) : ICapsuleerHandoff
    {
        public IReadOnlyList<SystemSimulationState> Systems() => actual.Systems();

        public void Synchronize(Action action) => actual.Synchronize(action);

        public ValueTask SynchronizeAsync(Action action, CancellationToken cancellationToken = default) =>
            actual.SynchronizeAsync(action, cancellationToken);

        public CapsuleerBatch Capture()
        {
            clock.Milliseconds += 3;
            return actual.Capture();
        }
    }

    private sealed class TimedScene(ICapsuleerScene actual, TestClock clock, bool fail) : ICapsuleerScene
    {
        public int Stamp => actual.Stamp;

        public int MaterializedPilots => actual.MaterializedPilots;

        public void Publish(MinerObservation observation) => actual.Publish(observation);

        public void Publish(HaulerObservation observation) => actual.Publish(observation);

        public void Apply(CapsuleerBatch batch)
        {
            clock.Milliseconds += 7;
            if (fail)
            {
                throw new InvalidOperationException("Presentation failed after measured work.");
            }
            actual.Apply(batch);
        }
    }

    private sealed class ManualWakeClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Elapsed.Ticks;

        private Action? _wake;

        public void Wake() => _wake!();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _wake = () => callback(state);
            return new WakeTimer();
        }

        private sealed class WakeTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
