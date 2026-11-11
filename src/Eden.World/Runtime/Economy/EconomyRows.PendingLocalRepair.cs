using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyPendingLocalRepair(
    long ModuleId,
    double Amount,
    long CompletesAt,
    Eden.Gameplay.Repair.RepairLayer Layer,
    long ShipId,
    long StartedAt
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectPendingLocalRepair(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyPendingLocalRepair>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldArmorRepair
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldArmorRepair.ModuleId=json_extract(value,'$.Id')
                    )
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldArmorRepair(
                        ModuleId,Amount,CompletesAt,Layer,ShipId,StartedAt
                    )
                    SELECT
                        json_extract(value,'$.ModuleId'),json_extract(value,'$.Amount'),json_extract(value,'$.CompletesAt'),
                        json_extract(value,'$.Layer'),json_extract(value,'$.ShipId'),json_extract(value,'$.StartedAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ModuleId) DO UPDATE SET
                        Amount=excluded.Amount,CompletesAt=excluded.CompletesAt,Layer=excluded.Layer,ShipId=excluded.ShipId,
                        StartedAt=excluded.StartedAt
                    """
                );
            }
        }
    }
}
