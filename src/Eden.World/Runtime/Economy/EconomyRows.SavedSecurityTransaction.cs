using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedSecurityTransaction(
    long Id,
    int CorporationId,
    double Modification,
    double NewValue,
    long NpcId,
    long PilotId,
    int SystemId,
    long Time,
    int TypeId
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedSecurityTransaction(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedSecurityTransaction>> changes
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
                    DELETE FROM WorldSecurityTransaction
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldSecurityTransaction.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldSecurityTransaction(
                        Id,CorporationId,Modification,NewValue,NpcId,PilotId,SystemId,Time,TypeId
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.CorporationId'),
                        json_extract(value,'$.Modification'),json_extract(value,'$.NewValue'),json_extract(value,'$.NpcId'),
                        json_extract(value,'$.PilotId'),json_extract(value,'$.SystemId'),json_extract(value,'$.Time'),
                        json_extract(value,'$.TypeId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        CorporationId=excluded.CorporationId,Modification=excluded.Modification,NewValue=excluded.NewValue,
                        NpcId=excluded.NpcId,PilotId=excluded.PilotId,SystemId=excluded.SystemId,Time=excluded.Time,
                        TypeId=excluded.TypeId
                    """
                );
            }
        }
    }
}
