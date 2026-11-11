using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedEngagement(long LowerPilotId, long HigherPilotId, long ActiveUntil, long ExpiresAt);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedEngagement(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedEngagement>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldEngagement
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldEngagement.LowerPilotId=json_extract(value,'$.Id') AND
                        WorldEngagement.HigherPilotId=json_extract(value,'$.Part')
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
                    INSERT INTO WorldEngagement(
                        LowerPilotId,HigherPilotId,ActiveUntil,ExpiresAt
                    )
                    SELECT
                        json_extract(value,'$.LowerPilotId'),json_extract(value,'$.HigherPilotId'),
                        json_extract(value,'$.ActiveUntil'),json_extract(value,'$.ExpiresAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(LowerPilotId,HigherPilotId) DO UPDATE SET
                        ActiveUntil=excluded.ActiveUntil,ExpiresAt=excluded.ExpiresAt
                    """
                );
            }
        }
    }
}
