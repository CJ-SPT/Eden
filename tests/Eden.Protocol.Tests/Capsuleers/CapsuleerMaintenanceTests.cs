using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Operations;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Maintenance;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerMaintenanceTests
{
    [Fact]
    public async Task OneWakeDrainsMultipleBoundedTurnsWithoutWaitingForAnotherTimerTick()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        ManualWakeClock clock = new();
        BackloggedScheduler scheduler = new();
        using CapsuleerMaintenance maintenance = new(
            scheduler,
            host.GetRequiredService<ICapsuleerHandoff>(),
            host.GetRequiredService<ICapsuleerScene>(),
            host.GetRequiredService<ChatRuntime>(),
            clock,
            host.GetRequiredService<IWorldSynchronization>()
        );
        maintenance.Start();
        clock.Wake();
        await scheduler.Current.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, scheduler.Calls);
    }

    private sealed class BackloggedScheduler : ICapsuleerScheduler
    {
        public TaskCompletionSource Current { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public CapsuleerBatch Run(int destinyStamp)
        {
            Calls++;
            if (Calls == 3)
            {
                Current.SetResult();
            }
            return new(new(CapsuleerJobQueue.ActionsPerTurn, 200, Calls < 3 ? 1 : null, true), [], []);
        }

        public IReadOnlyList<CapsuleerJob> Jobs() => [];

        public MinerObservation? Miner(long pilotId) => null;

        public HaulerObservation? Hauler(long pilotId) => null;
    }

    private sealed class ManualWakeClock : TimeProvider
    {
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
