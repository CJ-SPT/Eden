using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedWeaponLink(long SlaveId, long MasterId, long ShipId);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedWeaponLink(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedWeaponLink>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldWeaponLink
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldWeaponLink.SlaveId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldWeaponLink(
                        SlaveId,MasterId,ShipId
                    )
                    SELECT
                        json_extract(value,'$.SlaveId'),json_extract(value,'$.MasterId'),json_extract(value,'$.ShipId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(SlaveId) DO UPDATE SET
                        MasterId=excluded.MasterId,ShipId=excluded.ShipId
                    """
                );
            }
        }
    }
}
