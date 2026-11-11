using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedMiningSite(
    long Id,
    int DungeonId,
    long ExpiresAt,
    Eden.Gameplay.Mining.MiningFamily Family,
    long? ReplaceAt,
    int SystemId,
    double X,
    double Y,
    double Z
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedMiningSite(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMiningSite>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMiningSite
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldMiningSite.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldMiningSite(
                        Id,DungeonId,ExpiresAt,Family,ReplaceAt,SystemId,X,Y,Z
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.DungeonId'),json_extract(value,'$.ExpiresAt'),
                        json_extract(value,'$.Family'),json_extract(value,'$.ReplaceAt'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.X'),json_extract(value,'$.Y'),json_extract(value,'$.Z')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        DungeonId=excluded.DungeonId,ExpiresAt=excluded.ExpiresAt,Family=excluded.Family,
                        ReplaceAt=excluded.ReplaceAt,SystemId=excluded.SystemId,X=excluded.X,Y=excluded.Y,Z=excluded.Z
                    """
                );
            }
        }
    }
}
