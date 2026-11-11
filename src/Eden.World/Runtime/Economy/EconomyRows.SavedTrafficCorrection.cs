using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedTrafficCorrection(long PilotId, int OriginalHullTypeId, long ShipId, long TemplateKillId);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedTrafficCorrection(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedTrafficCorrection>> changes
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
                    DELETE FROM WorldTrafficCorrection
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldTrafficCorrection.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldTrafficCorrection(
                        PilotId,OriginalHullTypeId,ShipId,TemplateKillId
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.OriginalHullTypeId'),
                        json_extract(value,'$.ShipId'),json_extract(value,'$.TemplateKillId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        OriginalHullTypeId=excluded.OriginalHullTypeId,ShipId=excluded.ShipId,
                        TemplateKillId=excluded.TemplateKillId
                    """
                );
            }
        }
    }
}
