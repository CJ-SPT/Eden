using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyShipEnergy(long ShipId, double Charge, long UpdatedAt);

internal sealed partial class ResidentEconomy
{
    private static void ProjectShipEnergy(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyShipEnergy>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldShipEnergy
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldShipEnergy.ShipId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldShipEnergy(
                        ShipId,Charge,UpdatedAt
                    )
                    SELECT
                        json_extract(value,'$.ShipId'),json_extract(value,'$.Charge'),json_extract(value,'$.UpdatedAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ShipId) DO UPDATE SET
                        Charge=excluded.Charge,UpdatedAt=excluded.UpdatedAt
                    """
                );
            }
        }
    }
}
