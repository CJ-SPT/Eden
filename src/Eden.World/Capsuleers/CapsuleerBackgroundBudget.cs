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
    private readonly Dictionary<CapsuleerAction, TimeSpan> _costs = [];
    private static readonly TimeSpan MinimumAllowance = TimeSpan.FromMilliseconds(1);
    internal long Overruns { get; private set; }
    internal int PendingSystems { get; set; }

    internal IDisposable? TryEnter(string operation = "coarse simulation", CapsuleerAction? action = null)
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
            TimeSpan available = CapsuleerCoarseRules.Budget - _spent;
            TimeSpan estimate = action is { } kind ? _costs.GetValueOrDefault(kind, CapsuleerCoarseRules.Budget) : MinimumAllowance;
            if (available < estimate)
            {
                return null;
            }
            _busy = true;
            return new Lease(this, now, operation, available, action);
        }
    }

    private sealed class Lease(CapsuleerBackgroundBudget owner, long started, string operation, TimeSpan available, CapsuleerAction? action)
        : IDisposable
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
                if (action is { } kind)
                {
                    // Reserve the previous unit's cost, bounded by one full allowance.
                    // Expensive atomic work can still run, and still repays its debt.
                    owner._costs[kind] = TimeSpan.FromTicks(
                        Math.Clamp(elapsed.Ticks, MinimumAllowance.Ticks, CapsuleerCoarseRules.Budget.Ticks)
                    );
                }
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
