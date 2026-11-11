using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyNpcAggression(
    long NpcId,
    long? LastShotAt,
    long? LastTargetId,
    long MissileReadyAt,
    long ReadyAt,
    long TargetShipId
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectNpcAggression(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyNpcAggression>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldNpcAggression
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldNpcAggression.NpcId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldNpcAggression(
                        NpcId,LastShotAt,LastTargetId,MissileReadyAt,ReadyAt,TargetShipId
                    )
                    SELECT
                        json_extract(value,'$.NpcId'),json_extract(value,'$.LastShotAt'),
                        json_extract(value,'$.LastTargetId'),json_extract(value,'$.MissileReadyAt'),
                        json_extract(value,'$.ReadyAt'),json_extract(value,'$.TargetShipId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(NpcId) DO UPDATE SET
                        LastShotAt=excluded.LastShotAt,LastTargetId=excluded.LastTargetId,
                        MissileReadyAt=excluded.MissileReadyAt,ReadyAt=excluded.ReadyAt,TargetShipId=excluded.TargetShipId
                    """
                );
            }
        }
    }
}
