using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedMissionStructure(
    long NpcId,
    bool CompletesMission,
    long? DestroyedAt,
    bool LootEmpty,
    long MissionId,
    long ObjectId,
    bool Required
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedMissionStructure(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedMissionStructure>> changes
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
                    DELETE FROM WorldMissionStructure
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldMissionStructure.NpcId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldMissionStructure(
                        NpcId,CompletesMission,DestroyedAt,LootEmpty,MissionId,ObjectId,Required
                    )
                    SELECT
                        json_extract(value,'$.NpcId'),json_extract(value,'$.CompletesMission'),
                        json_extract(value,'$.DestroyedAt'),json_extract(value,'$.LootEmpty'),
                        json_extract(value,'$.MissionId'),json_extract(value,'$.ObjectId'),json_extract(value,'$.Required')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(NpcId) DO UPDATE SET
                        CompletesMission=excluded.CompletesMission,DestroyedAt=excluded.DestroyedAt,
                        LootEmpty=excluded.LootEmpty,MissionId=excluded.MissionId,ObjectId=excluded.ObjectId,
                        Required=excluded.Required
                    """
                );
            }
        }
    }
}
