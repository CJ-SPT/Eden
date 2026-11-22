using System.Text.Json;
using Eden.Data.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record StandardMarketType(int RegionId, int TypeId, long SnapshotTicks);

    private void RegisterStandardMarketTypes()
    {
        Register(
            new EconomyTable<SavedStandardMarketType, StandardMarketType>(
                "WorldStandardMarketType",
                row => new(row.RegionId, row.TypeId),
                row => new(row.RegionId, row.TypeId, row.SnapshotTicks),
                row => new SavedStandardMarketType
                {
                    RegionId = row.RegionId,
                    TypeId = row.TypeId,
                    SnapshotTicks = row.SnapshotTicks,
                },
                ProjectStandardMarketTypes,
                "RegionId,TypeId,SnapshotTicks",
                reader => new(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3)),
                row => new(row.RegionId, row.TypeId),
                indexes: [],
                primaryColumn: null
            )
        );
    }

    private static void ProjectStandardMarketTypes(WorldDbContext world, IReadOnlyList<EconomyChange<StandardMarketType>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var rows = batch.Select(change => change.Value!).ToArray();
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldStandardMarketType(RegionId,TypeId,SnapshotTicks)
                SELECT value->>'$.RegionId',value->>'$.TypeId',value->>'$.SnapshotTicks'
                FROM json_each({JsonSerializer.Serialize(rows)}) WHERE 1
                ON CONFLICT(RegionId,TypeId) DO UPDATE SET SnapshotTicks=excluded.SnapshotTicks
                """
            );
        }
    }
}
