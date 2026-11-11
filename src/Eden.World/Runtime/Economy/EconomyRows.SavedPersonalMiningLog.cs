using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedPersonalMiningLog(
    long Id,
    int CorporationId,
    long Critical,
    long Day,
    long PilotId,
    long Quantity,
    int SystemId,
    int TypeId,
    long Wasted
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedPersonalMiningLog(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedPersonalMiningLog>> changes
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
                    DELETE FROM WorldPersonalMiningLog
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldPersonalMiningLog.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldPersonalMiningLog(
                        Id,CorporationId,Critical,Day,PilotId,Quantity,SystemId,TypeId,Wasted
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.CorporationId'),json_extract(value,'$.Critical'),
                        json_extract(value,'$.Day'),json_extract(value,'$.PilotId'),json_extract(value,'$.Quantity'),
                        json_extract(value,'$.SystemId'),json_extract(value,'$.TypeId'),json_extract(value,'$.Wasted')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        CorporationId=excluded.CorporationId,Critical=excluded.Critical,Day=excluded.Day,
                        PilotId=excluded.PilotId,Quantity=excluded.Quantity,SystemId=excluded.SystemId,
                        TypeId=excluded.TypeId,Wasted=excluded.Wasted
                    """
                );
            }
        }
    }
}
