using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using Eden.Gameplay.Maps;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Maps;

internal sealed record MapStatisticsPayload(
    MapActivityEvent[] Events,
    MapPilotSample[] Samples,
    ImmutableDictionary<long, MapVisitHistory>? Visits = null
);

internal sealed record MapStatisticsCheckpoint(
    IReadOnlyList<MapActivityBatch> Events,
    ImmutableQueue<MapPilotSample> Samples,
    long Cutoff,
    ImmutableDictionary<long, MapVisitHistory> Visits
)
{
    private const int EventsPerBatch = 4096;
    private const int EventBytes = sizeof(long) + sizeof(int) + sizeof(int);

    internal static void Restore(WorldDbContext world, MapStatistics statistics, long now)
    {
        string? json = world.Database.SqlQueryRaw<string>("SELECT Payload AS Value FROM WorldMapStatistics WHERE Id=1").SingleOrDefault();
        if (json is not null && JsonSerializer.Deserialize<MapStatisticsPayload>(json) is { } saved)
        {
            statistics.Restore(saved, now);
        }
        foreach (
            byte[] batch in world.Database.SqlQueryRaw<byte[]>("SELECT Payload AS Value FROM WorldMapActivity ORDER BY Id").AsEnumerable()
        )
        {
            statistics.RestoreEvents(ReadEvents(batch), now);
        }
    }

    internal void Apply(WorldDbContext world)
    {
        // The detached image contains the prior base. Replace its history in the checkpoint transaction.
        world.Database.ExecuteSqlRaw("DELETE FROM WorldMapActivity");
        int id = 0;
        int offset = 0;
        byte[]? batch = null;
        foreach (var activity in Events.SelectMany(events => events.Read()))
        {
            if (activity.Time <= Cutoff)
            {
                continue;
            }
            batch ??= new byte[EventsPerBatch * EventBytes];
            var record = batch.AsSpan(offset, EventBytes);
            BinaryPrimitives.WriteInt64LittleEndian(record, activity.Time);
            BinaryPrimitives.WriteInt32LittleEndian(record[sizeof(long)..], activity.SystemId);
            BinaryPrimitives.WriteInt32LittleEndian(record[(sizeof(long) + sizeof(int))..], (int)activity.Kind);
            offset += EventBytes;
            if (offset == batch.Length)
            {
                world.Database.ExecuteSql($"INSERT INTO WorldMapActivity(Id,Payload) VALUES({++id},{batch})");
                offset = 0;
            }
        }
        if (offset != 0)
        {
            world.Database.ExecuteSql($"INSERT INTO WorldMapActivity(Id,Payload) VALUES({++id},{batch![..offset]})");
        }
        if (Samples.IsEmpty && Visits.IsEmpty)
        {
            world.Database.ExecuteSqlRaw("DELETE FROM WorldMapStatistics WHERE Id=1");
            return;
        }
        string json = JsonSerializer.Serialize(new MapStatisticsPayload([], [.. Samples], Visits));
        world.Database.ExecuteSql(
            $"INSERT INTO WorldMapStatistics(Id,Payload) VALUES(1,{json}) ON CONFLICT(Id) DO UPDATE SET Payload=excluded.Payload"
        );
    }

    private static IEnumerable<MapActivityEvent> ReadEvents(byte[] batch)
    {
        if (batch.Length == 0 || batch.Length > EventsPerBatch * EventBytes || batch.Length % EventBytes != 0)
        {
            throw new InvalidDataException("Invalid map activity checkpoint batch length.");
        }
        for (int offset = 0; offset < batch.Length; offset += EventBytes)
        {
            var record = batch.AsSpan(offset, EventBytes);
            yield return new(
                BinaryPrimitives.ReadInt64LittleEndian(record),
                BinaryPrimitives.ReadInt32LittleEndian(record[sizeof(long)..]),
                (MapActivityKind)BinaryPrimitives.ReadInt32LittleEndian(record[(sizeof(long) + sizeof(int))..])
            );
        }
    }
}
