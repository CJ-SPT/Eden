using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedLogoff(long PilotId, long ExpiresAt, long ShipId, int SystemId);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedLogoff(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedLogoff>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldLogoff
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldLogoff.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldLogoff(
                        PilotId,ExpiresAt,ShipId,SystemId
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.ExpiresAt'),json_extract(value,'$.ShipId'),
                        json_extract(value,'$.SystemId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        ExpiresAt=excluded.ExpiresAt,ShipId=excluded.ShipId,SystemId=excluded.SystemId
                    """
                );
            }
        }
    }
}
