using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCharacterDeletion(long PilotId, long? DeletedAt, long ReadyAt);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCharacterDeletion(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCharacterDeletion>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCharacterDeletion
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCharacterDeletion.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCharacterDeletion(
                        PilotId,DeletedAt,ReadyAt
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.DeletedAt'),json_extract(value,'$.ReadyAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        DeletedAt=excluded.DeletedAt,ReadyAt=excluded.ReadyAt
                    """
                );
            }
        }
    }
}
