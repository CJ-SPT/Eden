using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedStructureAccess(long StructureId, long MemberId, bool Allowed);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedStructureAccess(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedStructureAccess>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldStructureAccess
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldStructureAccess.StructureId=json_extract(value,'$.Id') AND
                        WorldStructureAccess.MemberId=json_extract(value,'$.Part')
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
                    INSERT INTO WorldStructureAccess(
                        StructureId,MemberId,Allowed
                    )
                    SELECT
                        json_extract(value,'$.StructureId'),json_extract(value,'$.MemberId'),json_extract(value,'$.Allowed')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(StructureId,MemberId) DO UPDATE SET
                        Allowed=excluded.Allowed
                    """
                );
            }
        }
    }
}
