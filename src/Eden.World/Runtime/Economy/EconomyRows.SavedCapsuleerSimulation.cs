using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCapsuleerSimulation(int SystemId, long StartedTicks);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCapsuleerSimulation(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCapsuleerSimulation>> changes
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
                    DELETE FROM WorldCapsuleerSimulation
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCapsuleerSimulation.SystemId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCapsuleerSimulation(
                        SystemId,StartedTicks
                    )
                    SELECT
                        json_extract(value,'$.SystemId'),json_extract(value,'$.StartedTicks')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(SystemId) DO UPDATE SET
                        StartedTicks=excluded.StartedTicks
                    """
                );
            }
        }
    }
}
