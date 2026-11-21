using System.Threading.Channels;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerPopulationRetryTests
{
    private const int Source = 30000141;
    private const int Other = 31000001;

    [Fact]
    public async Task FailedSystemRetriesWithoutCompletingOrBlockingAnotherOccupiedSystem()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerProvisioningTests.AddSystem(fixture.Database, Other);
        CapsuleerBackgroundPreparationTests.PrepareStartup(fixture);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Source);
        FaultingSynchronization synchronization = new(host.GetRequiredService<IWorldSynchronization>());
        using var preparation = ActivatorUtilities.CreateInstance<CapsuleerBackgroundPreparation>(host, synchronization);
        Assert.Null(preparation.PrepareNext().Failure);
        var live = host.GetRequiredService<LiveWorld>();
        long first = Assert.Single(Agents(live, Source));
        synchronization.FailingSystem = Source;
        var failed = preparation.PrepareNext();
        Assert.NotNull(failed.Failure);
        Assert.False(failed.Complete);
        Assert.Equal(0, failed.Progress.CompletedSystems);
        Assert.Equal(1, failed.Progress.DeferredSystems);
        Assert.Equal(new[] { first }, Agents(live, Source));
        using var other = host.GetRequiredService<IHumanPresence>().Select(90000002, Other);
        var healthy = preparation.PrepareNext();
        Assert.Equal(Other, healthy.SystemId);
        Assert.Null(healthy.Failure);
        Assert.NotEmpty(Agents(live, Other));
        CapsuleerBackgroundTurn waiting = healthy;
        for (int step = 0; step < 64 && !waiting.RetryLater; step++)
        {
            waiting = preparation.PrepareNext();
        }
        Assert.True(waiting.RetryLater);
        Assert.False(waiting.Complete);
        Assert.Equal(CapsuleerRetry.InitialDelay, waiting.RetryAfter);
        using (var metrics = SessionPerformance.Begin("cooling population retries", _ => { }, force: true)!)
        {
            for (int step = 0; step < 100; step++)
            {
                Assert.Equal(CapsuleerRetry.InitialDelay, preparation.PrepareNext().RetryAfter);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.Equal(1, synchronization.Failures);
        fixture.Clock.Milliseconds += 999;
        Assert.Null(preparation.PrepareNext().SystemId);
        fixture.Clock.Milliseconds++;
        Assert.NotNull(preparation.PrepareNext().Failure);
        Assert.Equal(TimeSpan.FromSeconds(2), preparation.PrepareNext().RetryAfter);
        synchronization.FailingSystem = null;
        fixture.Clock.Seconds += 2;
        var recovered = preparation.PrepareNext();
        Assert.Equal(Source, recovered.SystemId);
        Assert.Null(recovered.Failure);
        Assert.Equal(0, recovered.Progress.DeferredSystems);
        Assert.Equal(2, Agents(live, Source).Length);
        Assert.Contains(first, Agents(live, Source));
        await live.FlushAsync();
        var saved = Agents(live, Source);
        host.Dispose();
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        Assert.Equal(saved, Agents(reopened.GetRequiredService<LiveWorld>(), Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedVisitorRetriesTheSameCursorWithoutDuplicatingEarlierVisitors(bool observerLeaves)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        WorldConfigurationStore configuration = new(fixture.Database, fixture.Clock);
        var state = configuration.Read();
        configuration.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with { Capsuleers = new(Miners: false, TrafficMultiplier: 0, GateVisitors: 3) },
            },
            "visitor retry test"
        );
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var ships = host.GetRequiredService<SqliteWorld>();
        var pilot = ships.FindPilot(1000001)!;
        var gate = host.GetRequiredService<ISystemObjects>().Load(Source).First(value => value.DestinationSystemId is not null);
        ships.SetPosition(pilot.ShipId, gate.Position, Source);
        var activity = host.GetRequiredService<CapsuleerActivitySource>();
        Guid receiver = Guid.NewGuid();
        activity.Update(receiver, Source, pilot.PilotId);
        Guid remote = Guid.NewGuid();
        activity.Update(remote, gate.DestinationSystemId!.Value, 90000002);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        scheduler.Capture();
        FaultingSynchronization synchronization = new(host.GetRequiredService<IWorldSynchronization>());
        using var preparation = ActivatorUtilities.CreateInstance<CapsuleerBackgroundPreparation>(host, synchronization);
        Assert.Null(preparation.PrepareNext().Failure);
        var live = host.GetRequiredService<LiveWorld>();
        var first = live.Economy.Inspect(world => world.Set<SavedCapsuleerAgent>().Select(agent => agent.PilotId).Single());
        var pending = Assert.IsType<CapsuleerScheduler.InboundPreparation>(scheduler.NextInboundPreparation());
        Assert.Equal(1, pending.Visitor);
        if (observerLeaves)
        {
            synchronization.BeforeInbound = () => activity.Update(receiver, null);
            Assert.Null(preparation.PrepareNext().Failure);
            Assert.Null(scheduler.NextInboundPreparation());
            synchronization.BeforeInbound = null;
            activity.Update(receiver, Source, pilot.PilotId);
            Assert.Equal(pending, scheduler.NextInboundPreparation());
        }
        else
        {
            synchronization.FailInbound = true;
            Assert.NotNull(preparation.PrepareNext().Failure);
            Assert.Equal(pending, scheduler.NextInboundPreparation());
            CapsuleerBackgroundTurn waiting = preparation.PrepareNext();
            for (int step = 0; step < 16 && !waiting.RetryLater; step++)
            {
                waiting = preparation.PrepareNext();
            }
            Assert.False(waiting.Complete);
            Assert.Equal(CapsuleerRetry.InitialDelay, waiting.RetryAfter);
            Assert.Equal(1, synchronization.Failures);
            synchronization.FailInbound = false;
            fixture.Clock.Seconds++;
        }
        Assert.Null(preparation.PrepareNext().Failure);
        Assert.Equal(2, scheduler.NextInboundPreparation()!.Visitor);
        Assert.Null(preparation.PrepareNext().Failure);
        Assert.Null(scheduler.NextInboundPreparation());
        var agents = live.Economy.Inspect(world => world.Set<SavedCapsuleerAgent>().Select(agent => agent.PilotId).ToArray());
        Assert.Equal(3, agents.Length);
        Assert.Equal(3, agents.Distinct().Count());
        Assert.Contains(first, agents);
    }

    [Fact]
    public void CancellationDoesNotDeferOrCompleteTheSystemAndLeavingDiscardsRetryState()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerBackgroundPreparationTests.PrepareStartup(fixture);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var activity = host.GetRequiredService<CapsuleerActivitySource>();
        Guid session = Guid.NewGuid();
        activity.Update(session, Source);
        FaultingSynchronization synchronization = new(host.GetRequiredService<IWorldSynchronization>())
        {
            FailingSystem = Source,
            Cancel = true,
        };
        using var preparation = ActivatorUtilities.CreateInstance<CapsuleerBackgroundPreparation>(host, synchronization);
        Assert.Throws<OperationCanceledException>(() => preparation.PrepareNext());
        synchronization.Cancel = false;
        Assert.NotNull(preparation.PrepareNext().Failure);
        Assert.Equal(CapsuleerRetry.InitialDelay, preparation.PrepareNext().RetryAfter);
        activity.Update(session, null);
        Assert.True(preparation.PrepareNext().Complete);
        synchronization.FailingSystem = null;
        activity.Update(session, Source);
        Assert.Null(preparation.PrepareNext().Failure);
        Assert.NotEmpty(Agents(host.GetRequiredService<LiveWorld>(), Source));
    }

    [Fact]
    public void BusySystemYieldsPreparationToAnotherOccupiedSystem()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerProvisioningTests.AddSystem(fixture.Database, Other);
        CapsuleerBackgroundPreparationTests.PrepareStartup(fixture);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var first = host.GetRequiredService<IHumanPresence>().Select(90000001, Source);
        using var second = host.GetRequiredService<IHumanPresence>().Select(90000002, Other);
        FaultingSynchronization synchronization = new(host.GetRequiredService<IWorldSynchronization>()) { BusySystem = Source };
        using var preparation = ActivatorUtilities.CreateInstance<CapsuleerBackgroundPreparation>(host, synchronization);
        Assert.True(preparation.PrepareNext().RetryLater);
        var next = preparation.PrepareNext();
        Assert.Equal(Other, next.SystemId);
        Assert.Null(next.Failure);
        Assert.NotEmpty(Agents(host.GetRequiredService<LiveWorld>(), Other));
    }

    [Fact]
    public async Task WorkerWaitsForRetryDeadlineAndMembershipChangesCancelTheOldTimer()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        RetryPreparation preparation = new();
        ControlledClock clock = new();
        using CapsuleerPopulationMaintenance worker = new(preparation, clock, host.GetRequiredService<IPilotOperationScope>());
        worker.Start();
        var first = await clock.NextTimer();
        Assert.Equal(TimeSpan.FromSeconds(30), first.Delay);
        Assert.Equal(1, preparation.Calls);
        preparation.Reschedule();
        var earlier = await clock.NextTimer();
        Assert.Equal(TimeSpan.FromSeconds(1), earlier.Delay);
        Assert.True(first.Disposed);
        Assert.Equal(2, preparation.Calls);
        clock.Elapsed = TimeSpan.FromSeconds(1);
        earlier.Fire();
        var retry = await clock.NextTimer();
        Assert.Equal(3, preparation.Calls);
        worker.Dispose();
        Assert.True(retry.Disposed);
    }

    private static long[] Agents(LiveWorld live, int system) =>
        live.Economy.Inspect(world =>
            world
                .Set<SavedCapsuleerAgent>()
                .Where(agent => agent.CommunitySystemId == system)
                .Select(agent => agent.PilotId)
                .Order()
                .ToArray()
        );

    private sealed class FaultingSynchronization(IWorldSynchronization inner) : IWorldSynchronization
    {
        internal int? FailingSystem { get; set; }
        internal int? BusySystem { get; set; }
        internal bool FailInbound { get; set; }
        internal bool Cancel { get; set; }
        internal Action? BeforeInbound { get; set; }
        internal int Failures { get; private set; }
        public bool IsEntered => inner.IsEntered;

        public IDisposable Enter(string operation, int? systemId = null)
        {
            if (operation == "inbound prepared founder")
            {
                BeforeInbound?.Invoke();
            }
            if (FailInbound && operation == "inbound prepared founder")
            {
                Fail();
            }
            return inner.Enter(operation, systemId);
        }

        public IDisposable? TryEnterShared(string operation) => inner.TryEnterShared(operation);

        public IDisposable? TryEnterSystems(string operation, IReadOnlyCollection<int> systems)
        {
            if (FailingSystem is int failed && systems.Contains(failed))
            {
                Fail();
            }
            return BusySystem is int busy && systems.Contains(busy) ? null : inner.TryEnterSystems(operation, systems);
        }

        private void Fail()
        {
            Failures++;
            if (Cancel)
            {
                throw new OperationCanceledException();
            }
            throw new IOException("Population boundary temporarily unavailable.");
        }
    }

    private sealed class RetryPreparation : ICapsuleerBackgroundPreparation
    {
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public Task Changed => _changed.Task;
        private TimeSpan _delay = TimeSpan.FromSeconds(30);

        internal void Reschedule()
        {
            _delay = TimeSpan.FromSeconds(1);
            var previous = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }

        public CapsuleerBackgroundTurn PrepareNext(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return new(new(0, 1, 1), null, false) { RetryLater = true, RetryAfter = _delay };
        }
    }

    private sealed class ControlledClock : TimeProvider
    {
        private readonly Channel<ControlledTimer> _timers = Channel.CreateUnbounded<ControlledTimer>();
        internal TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Elapsed.Ticks;

        internal Task<ControlledTimer> NextTimer() => _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ControlledTimer timer = new(callback, state, dueTime);
            _timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        internal TimeSpan Delay { get; } = delay;
        internal bool Disposed { get; private set; }

        internal void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
