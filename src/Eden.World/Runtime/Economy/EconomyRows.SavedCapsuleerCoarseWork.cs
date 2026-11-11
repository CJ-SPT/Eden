using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCapsuleerCoarseWork(int SystemId, long Cursor, long Generation, long NextTicks, long SettledTicks);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCapsuleerCoarseWork(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCapsuleerCoarseWork>> changes
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
                    DELETE FROM WorldCapsuleerCoarseWork
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCapsuleerCoarseWork.SystemId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCapsuleerCoarseWork(
                        SystemId,Cursor,Generation,NextTicks,SettledTicks
                    )
                    SELECT
                        json_extract(value,'$.SystemId'),json_extract(value,'$.Cursor'),json_extract(value,'$.Generation'),
                        json_extract(value,'$.NextTicks'),json_extract(value,'$.SettledTicks')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(SystemId) DO UPDATE SET
                        Cursor=excluded.Cursor,Generation=excluded.Generation,NextTicks=excluded.NextTicks,
                        SettledTicks=excluded.SettledTicks
                    """
                );
            }
        }
    }
}
