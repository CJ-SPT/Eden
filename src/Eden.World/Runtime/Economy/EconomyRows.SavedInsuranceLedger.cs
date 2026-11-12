using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedInsuranceLedger(
    long Id,
    long AmountCents,
    long BalanceCents,
    bool IsPayout,
    long PilotId,
    long? PolicyId,
    long ShipId,
    long Time,
    int TypeId
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedInsuranceLedger(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedInsuranceLedger>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldInsuranceLedger
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldInsuranceLedger.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldInsuranceLedger(
                        Id,AmountCents,BalanceCents,IsPayout,PilotId,PolicyId,ShipId,Time,TypeId
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.AmountCents'),json_extract(value,'$.BalanceCents'),
                        json_extract(value,'$.IsPayout'),json_extract(value,'$.PilotId'),json_extract(value,'$.PolicyId'),
                        json_extract(value,'$.ShipId'),json_extract(value,'$.Time'),json_extract(value,'$.TypeId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        AmountCents=excluded.AmountCents,BalanceCents=excluded.BalanceCents,IsPayout=excluded.IsPayout,
                        PilotId=excluded.PilotId,PolicyId=excluded.PolicyId,ShipId=excluded.ShipId,Time=excluded.Time,
                        TypeId=excluded.TypeId
                    """
                );
            }
        }
    }
}
