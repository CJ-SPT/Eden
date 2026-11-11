using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCorporationTitle(int CorporationId, long TitleId, string DataJson);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCorporationTitle(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCorporationTitle>> changes
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
                    DELETE FROM WorldCorporationTitle
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCorporationTitle.CorporationId=json_extract(value,'$.Id') AND
                        WorldCorporationTitle.TitleId=json_extract(value,'$.Part')
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
                    INSERT INTO WorldCorporationTitle(
                        CorporationId,TitleId,DataJson
                    )
                    SELECT
                        json_extract(value,'$.CorporationId'),json_extract(value,'$.TitleId'),
                        json_extract(value,'$.DataJson')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(CorporationId,TitleId) DO UPDATE SET
                        DataJson=excluded.DataJson
                    """
                );
            }
        }
    }
}
