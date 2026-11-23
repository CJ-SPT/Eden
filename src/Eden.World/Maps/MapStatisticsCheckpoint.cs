using System.Collections.Immutable;
using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Maps;

internal sealed record MapStatisticsPayload(
    MapActivityEvent[] Events,
    MapPilotSample[] Samples,
    ImmutableDictionary<long, MapVisitHistory>? Visits = null
);

internal sealed record MapStatisticsCheckpoint(
    ImmutableSortedDictionary<long, ImmutableList<MapActivityEvent>> Events,
    ImmutableQueue<MapPilotSample> Samples,
    long Cutoff,
    ImmutableDictionary<long, MapVisitHistory> Visits
)
{
    internal static MapStatisticsPayload? Read(WorldDbContext world)
    {
        string? json = world.Database.SqlQueryRaw<string>("SELECT Payload AS Value FROM WorldMapStatistics WHERE Id=1").SingleOrDefault();
        return json is null ? null : JsonSerializer.Deserialize<MapStatisticsPayload>(json);
    }

    internal void Apply(WorldDbContext world)
    {
        if (Events.IsEmpty && Samples.IsEmpty && Visits.IsEmpty)
        {
            world.Database.ExecuteSqlRaw("DELETE FROM WorldMapStatistics WHERE Id=1");
            return;
        }
        string json = JsonSerializer.Serialize(
            new MapStatisticsPayload(
                Events.Values.SelectMany(events => events).Where(activity => activity.Time > Cutoff).ToArray(),
                [.. Samples],
                Visits
            )
        );
        world.Database.ExecuteSql(
            $"INSERT INTO WorldMapStatistics(Id,Payload) VALUES(1,{json}) ON CONFLICT(Id) DO UPDATE SET Payload=excluded.Payload"
        );
    }
}
