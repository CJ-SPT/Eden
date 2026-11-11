using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCapsuleerProvisioning(int Id, long WorldSeed);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCapsuleerProvisioning(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCapsuleerProvisioning>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCapsuleerProvisioning
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCapsuleerProvisioning.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCapsuleerProvisioning(
                        Id,WorldSeed
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.WorldSeed')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        WorldSeed=excluded.WorldSeed
                    """
                );
            }
        }
    }
}
