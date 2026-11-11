using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyActiveFittedModule(long ModuleId, long ExpiresAt, long ShipId);

internal sealed partial class ResidentEconomy
{
    private static void ProjectActiveFittedModule(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyActiveFittedModule>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldActiveModule
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldActiveModule.ModuleId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldActiveModule(
                        ModuleId,ExpiresAt,ShipId
                    )
                    SELECT
                        json_extract(value,'$.ModuleId'),json_extract(value,'$.ExpiresAt'),json_extract(value,'$.ShipId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ModuleId) DO UPDATE SET
                        ExpiresAt=excluded.ExpiresAt,ShipId=excluded.ShipId
                    """
                );
            }
        }
    }
}
