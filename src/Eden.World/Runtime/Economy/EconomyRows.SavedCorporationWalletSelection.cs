using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCorporationWalletSelection(long PilotId, int CorporationId, int Key);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCorporationWalletSelection(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCorporationWalletSelection>> changes
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
                    DELETE FROM WorldCorporationWalletSelection
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCorporationWalletSelection.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCorporationWalletSelection(
                        PilotId,CorporationId,Key
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.CorporationId'),json_extract(value,'$.Key')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        CorporationId=excluded.CorporationId,Key=excluded.Key
                    """
                );
            }
        }
    }
}
