using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCorporationMemberAccess(long PilotId, int CorporationId, string DataJson);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCorporationMemberAccess(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCorporationMemberAccess>> changes
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
                    DELETE FROM WorldCorporationMemberAccess
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCorporationMemberAccess.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCorporationMemberAccess(
                        PilotId,CorporationId,DataJson
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.CorporationId'),
                        json_extract(value,'$.DataJson')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        CorporationId=excluded.CorporationId,DataJson=excluded.DataJson
                    """
                );
            }
        }
    }
}
