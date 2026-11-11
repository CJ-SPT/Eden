using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldNpcEnergy(long NpcId, double Charge, long UpdatedAt);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldNpcEnergy(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldNpcEnergy>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldNpcEnergy
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldNpcEnergy.NpcId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldNpcEnergy(
                        NpcId,Charge,UpdatedAt
                    )
                    SELECT
                        json_extract(value,'$.NpcId'),json_extract(value,'$.Charge'),json_extract(value,'$.UpdatedAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(NpcId) DO UPDATE SET
                        Charge=excluded.Charge,UpdatedAt=excluded.UpdatedAt
                    """
                );
            }
        }
    }
}
