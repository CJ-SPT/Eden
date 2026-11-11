using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCapsuleerSuspension(long PilotId, long StartedTicks);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCapsuleerSuspension(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCapsuleerSuspension>> changes
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
                    DELETE FROM WorldCapsuleerSuspension
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCapsuleerSuspension.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCapsuleerSuspension(
                        PilotId,StartedTicks
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.StartedTicks')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        StartedTicks=excluded.StartedTicks
                    """
                );
            }
        }
    }
}
