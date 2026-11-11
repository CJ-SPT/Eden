using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedModuleCondition(long ModuleId, double Damage);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedModuleCondition(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedModuleCondition>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldModuleCondition
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldModuleCondition.ModuleId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldModuleCondition(
                        ModuleId,Damage
                    )
                    SELECT
                        json_extract(value,'$.ModuleId'),json_extract(value,'$.Damage')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ModuleId) DO UPDATE SET
                        Damage=excluded.Damage
                    """
                );
            }
        }
    }
}
