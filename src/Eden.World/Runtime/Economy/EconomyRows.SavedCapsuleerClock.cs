using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCapsuleerClock(int Id, long ElapsedTicks);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCapsuleerClock(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCapsuleerClock>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCapsuleerClock
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCapsuleerClock.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCapsuleerClock(
                        Id,ElapsedTicks
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.ElapsedTicks')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        ElapsedTicks=excluded.ElapsedTicks
                    """
                );
            }
        }
    }
}
