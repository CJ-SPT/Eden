using Eden.World.Maps;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    private static readonly TimeSpan MapSampleInterval = Maps.MapStatistics.SampleInterval;
    private readonly ITimer _mapStatisticsTimer;
    internal MapStatistics MapStatistics { get; } = new();

    private void SampleMapPilotsOnTimer()
    {
        try
        {
            SampleMapPilots();
        }
        catch (Exception error)
        {
            Report($"Map population sample failed: {error.Message}", failure: true);
        }
    }

    internal void SampleMapPilots()
    {
        using var ownership = EnterPersistence("map pilot sample");
        if (_stopping || _disposed || !Economy.IsLoaded)
        {
            return;
        }
        var players = CapsuleerActivity.Read().Pilots;
        if (MapStatistics.Sample(SimulationTime, Economy.MapPopulation(players)))
        {
            EconomicObjectsChanged();
        }
    }
}
