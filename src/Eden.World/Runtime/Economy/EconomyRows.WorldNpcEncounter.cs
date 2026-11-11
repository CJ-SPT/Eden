using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldNpcEncounter(
    long Id,
    int AnchorId,
    Eden.Gameplay.Combat.NpcAnchorKind AnchorKind,
    int FactionId,
    int Generation,
    double HomeX,
    double HomeY,
    double HomeZ,
    long? NextSpawnAt,
    bool Occupied,
    Eden.Gameplay.Combat.NpcRole Role,
    int SystemId,
    int Version
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldNpcEncounter(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldNpcEncounter>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldNpcEncounter
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldNpcEncounter.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldNpcEncounter(
                        Id,AnchorId,AnchorKind,FactionId,Generation,HomeX,HomeY,HomeZ,NextSpawnAt,Occupied,Role,SystemId,
                        Version
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.AnchorId'),json_extract(value,'$.AnchorKind'),
                        json_extract(value,'$.FactionId'),json_extract(value,'$.Generation'),json_extract(value,'$.HomeX'),
                        json_extract(value,'$.HomeY'),json_extract(value,'$.HomeZ'),json_extract(value,'$.NextSpawnAt'),
                        json_extract(value,'$.Occupied'),json_extract(value,'$.Role'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.Version')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        AnchorId=excluded.AnchorId,AnchorKind=excluded.AnchorKind,FactionId=excluded.FactionId,
                        Generation=excluded.Generation,HomeX=excluded.HomeX,HomeY=excluded.HomeY,HomeZ=excluded.HomeZ,
                        NextSpawnAt=excluded.NextSpawnAt,Occupied=excluded.Occupied,Role=excluded.Role,
                        SystemId=excluded.SystemId,Version=excluded.Version
                    """
                );
            }
        }
    }
}
