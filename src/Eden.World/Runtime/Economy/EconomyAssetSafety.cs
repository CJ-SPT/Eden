using System.Collections.Concurrent;
using System.Text.Json;
using Eden.Data.World.Structures;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyAssetSafety(
    long Id,
    long OwnerId,
    long StructureId,
    int SystemId,
    string Name,
    long CreatedAt,
    int NearestStationId,
    long? DestinationId,
    long? DeliveredAt
);

internal sealed partial class ResidentEconomy
{
    private readonly ConcurrentDictionary<long, long> _assetSafetyRevisions = new();
    private long _assetSafetyRevision;

    internal long AssetSafetyRevision(long owner) => _assetSafetyRevisions.GetValueOrDefault(owner);

    private void AssetSafetyChanged(EconomyAssetSafety? before, EconomyAssetSafety? after)
    {
        long revision = Interlocked.Increment(ref _assetSafetyRevision);
        if (before is not null)
        {
            _assetSafetyRevisions[before.OwnerId] = revision;
        }
        if (after is not null)
        {
            _assetSafetyRevisions[after.OwnerId] = revision;
        }
    }

    private void RegisterAssetSafety() =>
        Register(
            new EconomyTable<SavedAssetSafety, EconomyAssetSafety>(
                "WorldAssetSafety",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.OwnerId,
                        row.StructureId,
                        row.SystemId,
                        row.Name,
                        row.CreatedAt,
                        row.NearestStationId,
                        row.DestinationId,
                        row.DeliveredAt
                    ),
                state => new SavedAssetSafety
                {
                    Id = state.Id,
                    OwnerId = state.OwnerId,
                    StructureId = state.StructureId,
                    SystemId = state.SystemId,
                    Name = state.Name,
                    CreatedAt = state.CreatedAt,
                    NearestStationId = state.NearestStationId,
                    DestinationId = state.DestinationId,
                    DeliveredAt = state.DeliveredAt,
                },
                ProjectAssetSafety,
                "Id,OwnerId,StructureId,SystemId,Name,CreatedAt,NearestStationId,DestinationId,DeliveredAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        reader.GetString(5),
                        reader.GetInt64(6),
                        reader.GetInt32(7),
                        reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        reader.IsDBNull(9) ? null : reader.GetInt64(9)
                    ),
                state => new(state.Id),
                indexes: [new("OwnerId", row => row.OwnerId), new("SystemId", row => row.SystemId)],
                changed: AssetSafetyChanged,
                primaryColumn: "Id"
            )
        );

    private static void ProjectAssetSafety(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAssetSafety>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAssetSafety WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAssetSafety(Id,OwnerId,StructureId,SystemId,Name,CreatedAt,NearestStationId,DestinationId,DeliveredAt)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.OwnerId'),
                    json_extract(value,'$.StructureId'),
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.Name'),
                    json_extract(value,'$.CreatedAt'),
                    json_extract(value,'$.NearestStationId'),
                    json_extract(value,'$.DestinationId'),
                    json_extract(value,'$.DeliveredAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    OwnerId=excluded.OwnerId,
                    StructureId=excluded.StructureId,
                    SystemId=excluded.SystemId,
                    Name=excluded.Name,
                    CreatedAt=excluded.CreatedAt,
                    NearestStationId=excluded.NearestStationId,
                    DestinationId=excluded.DestinationId,
                    DeliveredAt=excluded.DeliveredAt
                """
            );
        }
    }
}
