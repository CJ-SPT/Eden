using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyShipHealthState(long ShipId, double ArmorDamage, double ShieldCharge, double StructureDamage, long UpdatedAt);

internal sealed partial class ResidentEconomy
{
    private static void ProjectShipHealthState(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyShipHealthState>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldShipHealth
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldShipHealth.ShipId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldShipHealth(
                        ShipId,ArmorDamage,ShieldCharge,StructureDamage,UpdatedAt
                    )
                    SELECT
                        json_extract(value,'$.ShipId'),json_extract(value,'$.ArmorDamage'),
                        json_extract(value,'$.ShieldCharge'),json_extract(value,'$.StructureDamage'),
                        json_extract(value,'$.UpdatedAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ShipId) DO UPDATE SET
                        ArmorDamage=excluded.ArmorDamage,ShieldCharge=excluded.ShieldCharge,
                        StructureDamage=excluded.StructureDamage,UpdatedAt=excluded.UpdatedAt
                    """
                );
            }
        }
    }
}
