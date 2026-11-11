using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCapsuleerPopulation(int SystemId, int HomeStationId, long Seed, int Version);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCapsuleerPopulation(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCapsuleerPopulation>> changes
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
                    DELETE FROM WorldCapsuleerPopulation
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCapsuleerPopulation.SystemId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCapsuleerPopulation(
                        SystemId,HomeStationId,Seed,Version
                    )
                    SELECT
                        json_extract(value,'$.SystemId'),json_extract(value,'$.HomeStationId'),json_extract(value,'$.Seed'),
                        json_extract(value,'$.Version')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(SystemId) DO UPDATE SET
                        HomeStationId=excluded.HomeStationId,Seed=excluded.Seed,Version=excluded.Version
                    """
                );
            }
        }
    }
}
