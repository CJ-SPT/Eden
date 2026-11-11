using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedMiningDeposit(long Id, long SiteId);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedMiningDeposit(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMiningDeposit>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMiningDeposit
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldMiningDeposit.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldMiningDeposit(
                        Id,SiteId
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.SiteId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        SiteId=excluded.SiteId
                    """
                );
            }
        }
    }
}
