using Eden.Gameplay.Capsuleers;

namespace Eden.Server.Capsuleers;

internal sealed class PopulationStartupProgress(Action<string> write, TimeProvider? clock = null)
{
    private const int BarWidth = 20;
    private const int PercentageScale = 100;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _lastFilled = -1;
    private long _lastReport;
    private int _lastCompleted = -1;

    internal void Report(CapsuleerPreparationProgress progress)
    {
        int filled = progress.TotalSystems == 0 ? BarWidth : (int)((long)progress.CompletedSystems * BarWidth / progress.TotalSystems);
        long now = _clock.GetTimestamp();
        if (
            progress.CompletedSystems == _lastCompleted
            || filled == _lastFilled && (filled == BarWidth || _clock.GetElapsedTime(_lastReport, now) < RefreshInterval)
        )
        {
            return;
        }
        _lastFilled = filled;
        _lastCompleted = progress.CompletedSystems;
        _lastReport = now;
        int percent =
            progress.TotalSystems == 0 ? PercentageScale : (int)((long)progress.CompletedSystems * PercentageScale / progress.TotalSystems);
        string bar = new string('#', filled) + new string('-', BarWidth - filled);
        // Plain log lines also work in redirected output, without terminal control sequences.
        write(
            $"NPC population [{bar}] {percent, 3}% "
                + $"({progress.CompletedSystems}/{progress.TotalSystems} systems checked; {progress.DeferredSystems} deferred)."
        );
    }
}
