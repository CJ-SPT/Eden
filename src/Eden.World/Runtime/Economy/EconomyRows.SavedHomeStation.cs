using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedHomeStation(long PilotId, int StationId);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedHomeStation(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedHomeStation>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldHomeStation
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldHomeStation.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldHomeStation(
                        PilotId,StationId
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.StationId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        StationId=excluded.StationId
                    """
                );
            }
        }
    }
}
