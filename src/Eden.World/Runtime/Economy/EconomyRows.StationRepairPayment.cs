using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyStationRepairPayment(
    long Id,
    long AmountCents,
    long BalanceCents,
    long PilotId,
    string RepairsJson,
    long StationId,
    long StationOwnerId,
    long Time
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectStationRepairPayment(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyStationRepairPayment>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldRepairPayment
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldRepairPayment.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldRepairPayment(
                        Id,AmountCents,BalanceCents,PilotId,RepairsJson,StationId,StationOwnerId,Time
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.AmountCents'),json_extract(value,'$.BalanceCents'),
                        json_extract(value,'$.PilotId'),json_extract(value,'$.RepairsJson'),
                        json_extract(value,'$.StationId'),json_extract(value,'$.StationOwnerId'),
                        json_extract(value,'$.Time')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        AmountCents=excluded.AmountCents,BalanceCents=excluded.BalanceCents,PilotId=excluded.PilotId,
                        RepairsJson=excluded.RepairsJson,StationId=excluded.StationId,
                        StationOwnerId=excluded.StationOwnerId,Time=excluded.Time
                    """
                );
            }
        }
    }
}
