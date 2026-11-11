using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyModuleCooldown(long ModuleId, Eden.Data.World.Combat.ModuleCooldownKind Kind, long ReadyAt);

internal sealed partial class ResidentEconomy
{
    private static void ProjectModuleCooldown(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyModuleCooldown>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldWeaponCooldown
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldWeaponCooldown.ModuleId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldWeaponCooldown(
                        ModuleId,Kind,ReadyAt
                    )
                    SELECT
                        json_extract(value,'$.ModuleId'),json_extract(value,'$.Kind'),json_extract(value,'$.ReadyAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ModuleId) DO UPDATE SET
                        Kind=excluded.Kind,ReadyAt=excluded.ReadyAt
                    """
                );
            }
        }
    }
}
