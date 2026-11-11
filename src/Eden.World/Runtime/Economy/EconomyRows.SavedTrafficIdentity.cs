using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedTrafficIdentity(long PilotId, long CharacterId, long KillId, string SnapshotId);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedTrafficIdentity(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedTrafficIdentity>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldTrafficIdentity
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldTrafficIdentity.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldTrafficIdentity(
                        PilotId,CharacterId,KillId,SnapshotId
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.CharacterId'),json_extract(value,'$.KillId'),
                        json_extract(value,'$.SnapshotId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        CharacterId=excluded.CharacterId,KillId=excluded.KillId,SnapshotId=excluded.SnapshotId
                    """
                );
            }
        }
    }
}
