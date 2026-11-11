using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerBackgroundBudget(TimeProvider clock)
{
    private readonly TimeProvider _clock = clock;
    private readonly Lock _gate = new();
    private long? _window;
    private TimeSpan _spent;
    private bool _busy;
    internal long Overruns { get; private set; }
    internal int PendingSystems { get; set; }

    internal IDisposable? TryEnter(string operation = "coarse simulation")
    {
        lock (_gate)
        {
            if (_busy)
            {
                return null;
            }
            long now = _clock.GetTimestamp();
            if (_window is null)
            {
                _window = now;
                _spent = TimeSpan.Zero;
            }
            else if (_clock.GetElapsedTime(_window.Value, now) is { } elapsed && elapsed >= CapsuleerCoarseRules.Window)
            {
                long windows = elapsed.Ticks / CapsuleerCoarseRules.Window.Ticks;
                // Atomic overruns borrow future allowance. Never discard debt at
                // the next timer window or bank unused time for a later burst.
                _spent = TimeSpan.FromTicks(Math.Max(0, _spent.Ticks - windows * CapsuleerCoarseRules.Budget.Ticks));
                _window = now;
            }
            if (_spent >= CapsuleerCoarseRules.Budget)
            {
                return null;
            }
            _busy = true;
            return new Lease(this, now, operation, CapsuleerCoarseRules.Budget - _spent);
        }
    }

    private sealed class Lease(CapsuleerBackgroundBudget owner, long started, string operation, TimeSpan available) : IDisposable
    {
        private bool _disposed;
        private readonly SessionPerformance _metrics = SessionPerformance.Begin("background unit", _ => { }, force: true)!;

        public void Dispose()
        {
            TimeSpan elapsed;
            bool overrun;
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                elapsed = owner._clock.GetElapsedTime(started);
                owner._spent += elapsed;
                overrun = owner._spent > CapsuleerCoarseRules.Budget;
                if (overrun)
                {
                    owner.Overruns++;
                }
                owner._busy = false;
            }
            _metrics.Dispose();
            if (overrun)
            {
                ServerLog.Console.Warning(
                    $"Background budget overrun ({operation}): unit={elapsed.TotalMilliseconds:F2} ms, "
                        + $"available={available.TotalMilliseconds:F2} ms, SQL={_metrics.Commands}, "
                        + $"connections={_metrics.Connections}, fits={_metrics.FittingCalculations}. "
                        + "Subsequent remote work is postponed."
                );
            }
        }
    }
}
