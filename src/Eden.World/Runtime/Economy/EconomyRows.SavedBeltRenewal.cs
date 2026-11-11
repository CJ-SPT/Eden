using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedBeltRenewal(int BeltId, long NextRenewal, int SystemId);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedBeltRenewal(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedBeltRenewal>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldBeltRenewal
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldBeltRenewal.BeltId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldBeltRenewal(
                        BeltId,NextRenewal,SystemId
                    )
                    SELECT
                        json_extract(value,'$.BeltId'),json_extract(value,'$.NextRenewal'),json_extract(value,'$.SystemId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(BeltId) DO UPDATE SET
                        NextRenewal=excluded.NextRenewal,SystemId=excluded.SystemId
                    """
                );
            }
        }
    }
}
