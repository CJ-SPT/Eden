using Eden.World.Maps;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    private static readonly TimeSpan MapSampleInterval = Maps.MapStatistics.SampleInterval;
    private readonly ITimer _mapStatisticsTimer;
    private int _mapSampleRunning;
    internal MapStatistics MapStatistics { get; } = new();

    internal void SampleMapPilotsOnTimer()
    {
        // Keep one sample pending while world ownership is busy. Timer ticks can overlap.
        if (Interlocked.Exchange(ref _mapSampleRunning, 1) != 0)
        {
            return;
        }
        try
        {
            SampleMapPilots();
        }
        catch (Exception error)
        {
            Report($"Map population sample failed: {error.Message}", failure: true);
        }
        finally
        {
            Volatile.Write(ref _mapSampleRunning, 0);
        }
    }

    internal void SampleMapPilots()
    {
        using var ownership = EnterPersistence("map pilot sample");
        if (_stopping || _disposed || !Economy.IsLoaded)
        {
            return;
        }
        var activity = CapsuleerActivity.Read();
        if (MapStatistics.Sample(SimulationTime, Economy.MapPopulation(activity)))
        {
            EconomicObjectsChanged();
        }
    }
}
