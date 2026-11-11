using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedMissionMember(
    long NpcId,
    bool CompletesMission,
    int LootQuantity,
    int LootTypeId,
    long MissionId,
    bool Required,
    bool Trigger,
    int Wave
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedMissionMember(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMissionMember>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMissionMember
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldMissionMember.NpcId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldMissionMember(
                        NpcId,CompletesMission,LootQuantity,LootTypeId,MissionId,Required,Trigger,Wave
                    )
                    SELECT
                        json_extract(value,'$.NpcId'),json_extract(value,'$.CompletesMission'),
                        json_extract(value,'$.LootQuantity'),json_extract(value,'$.LootTypeId'),
                        json_extract(value,'$.MissionId'),json_extract(value,'$.Required'),json_extract(value,'$.Trigger'),
                        json_extract(value,'$.Wave')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(NpcId) DO UPDATE SET
                        CompletesMission=excluded.CompletesMission,LootQuantity=excluded.LootQuantity,
                        LootTypeId=excluded.LootTypeId,MissionId=excluded.MissionId,Required=excluded.Required,
                        Trigger=excluded.Trigger,Wave=excluded.Wave
                    """
                );
            }
        }
    }
}
