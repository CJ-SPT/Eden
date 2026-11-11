using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedShipHeat(long ShipId, string StateJson);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedShipHeat(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedShipHeat>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldShipHeat
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldShipHeat.ShipId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldShipHeat(
                        ShipId,StateJson
                    )
                    SELECT
                        json_extract(value,'$.ShipId'),json_extract(value,'$.StateJson')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ShipId) DO UPDATE SET
                        StateJson=excluded.StateJson
                    """
                );
            }
        }
    }
}
