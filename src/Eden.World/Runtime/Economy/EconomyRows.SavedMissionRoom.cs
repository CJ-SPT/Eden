using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedMissionRoom(long MissionId, int RoomId, int Wave);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedMissionRoom(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMissionRoom>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMissionRoom
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldMissionRoom.MissionId=json_extract(value,'$.Id') AND WorldMissionRoom.RoomId=json_extract(value,'$.Part')
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
                    INSERT INTO WorldMissionRoom(
                        MissionId,RoomId,Wave
                    )
                    SELECT
                        json_extract(value,'$.MissionId'),json_extract(value,'$.RoomId'),json_extract(value,'$.Wave')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(MissionId,RoomId) DO UPDATE SET
                        Wave=excluded.Wave
                    """
                );
            }
        }
    }
}
