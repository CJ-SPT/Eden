using Eden.Gameplay.Maps;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Maps;

[Injectable]
internal sealed class MapStatisticsStore(WorldDatabase database, TimeProvider clock) : IMapStatistics
{
    public MapHistorySnapshot History(MapHistoryStatistic statistic, int hours) =>
        LiveWorld.Find(database.Path)?.MapStatistics.History(statistic, hours, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
        ?? MapHistorySnapshot.Empty;

    public MapPilotPopulation Population() =>
        LiveWorld.Find(database.Path)?.MapStatistics.Population(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()) ?? MapPilotPopulation.Empty;

    internal static void Record(WorldAction world, int systemId, long time, MapActivityKind kind)
    {
        if (MapStatistics.IsKnownSpace(systemId) && LiveWorld.Find(world.WorldPath) is { } live)
        {
            world.AfterCommit(() =>
            {
                live.MapStatistics.Record(new(time, systemId, kind));
                live.EconomicObjectsChanged();
            });
        }
    }
}
