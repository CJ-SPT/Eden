using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyNpcBounty(
    long Id,
    long AmountCents,
    long BalanceCents,
    long NpcId,
    long PilotId,
    int SystemId,
    long TaxCents,
    long Time,
    int TypeId
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectNpcBounty(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyNpcBounty>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldNpcBounty
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldNpcBounty.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldNpcBounty(
                        Id,AmountCents,BalanceCents,NpcId,PilotId,SystemId,TaxCents,Time,TypeId
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.AmountCents'),json_extract(value,'$.BalanceCents'),
                        json_extract(value,'$.NpcId'),json_extract(value,'$.PilotId'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.TaxCents'),json_extract(value,'$.Time'),json_extract(value,'$.TypeId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        AmountCents=excluded.AmountCents,BalanceCents=excluded.BalanceCents,NpcId=excluded.NpcId,
                        PilotId=excluded.PilotId,SystemId=excluded.SystemId,TaxCents=excluded.TaxCents,Time=excluded.Time,
                        TypeId=excluded.TypeId
                    """
                );
            }
        }
    }
}
