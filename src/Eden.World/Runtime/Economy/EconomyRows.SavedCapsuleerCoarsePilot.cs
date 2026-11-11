using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCapsuleerCoarsePilot(long PilotId, long SettledTicks);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCapsuleerCoarsePilot(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCapsuleerCoarsePilot>> changes
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
                    DELETE FROM WorldCapsuleerCoarsePilot
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCapsuleerCoarsePilot.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCapsuleerCoarsePilot(
                        PilotId,SettledTicks
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.SettledTicks')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        SettledTicks=excluded.SettledTicks
                    """
                );
            }
        }
    }
}
