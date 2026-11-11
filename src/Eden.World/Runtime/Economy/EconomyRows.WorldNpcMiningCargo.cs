using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldNpcMiningCargo(long NpcId, int TypeId, long Quantity);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldNpcMiningCargo(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldNpcMiningCargo>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldNpcMiningCargo
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldNpcMiningCargo.NpcId=json_extract(value,'$.Id') AND
                        WorldNpcMiningCargo.TypeId=json_extract(value,'$.Part')
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
                    INSERT INTO WorldNpcMiningCargo(
                        NpcId,TypeId,Quantity
                    )
                    SELECT
                        json_extract(value,'$.NpcId'),json_extract(value,'$.TypeId'),json_extract(value,'$.Quantity')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(NpcId,TypeId) DO UPDATE SET
                        Quantity=excluded.Quantity
                    """
                );
            }
        }
    }
}
