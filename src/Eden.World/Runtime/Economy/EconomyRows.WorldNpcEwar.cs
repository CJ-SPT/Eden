using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldNpcEwar(
    long NpcId,
    Eden.Gameplay.Combat.NpcEwarKind Kind,
    string AbilityJson,
    string? AppliedJson,
    long ReadyAt,
    long StartedAt,
    long TargetShipId,
    int TypeId
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldNpcEwar(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldNpcEwar>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldNpcEwar
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldNpcEwar.NpcId=json_extract(value,'$.Id') AND WorldNpcEwar.Kind=json_extract(value,'$.Part')
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
                    INSERT INTO WorldNpcEwar(
                        NpcId,Kind,AbilityJson,AppliedJson,ReadyAt,StartedAt,TargetShipId,TypeId
                    )
                    SELECT
                        json_extract(value,'$.NpcId'),json_extract(value,'$.Kind'),json_extract(value,'$.AbilityJson'),
                        json_extract(value,'$.AppliedJson'),json_extract(value,'$.ReadyAt'),
                        json_extract(value,'$.StartedAt'),json_extract(value,'$.TargetShipId'),
                        json_extract(value,'$.TypeId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(NpcId,Kind) DO UPDATE SET
                        AbilityJson=excluded.AbilityJson,AppliedJson=excluded.AppliedJson,ReadyAt=excluded.ReadyAt,
                        StartedAt=excluded.StartedAt,TargetShipId=excluded.TargetShipId,TypeId=excluded.TypeId
                    """
                );
            }
        }
    }
}
