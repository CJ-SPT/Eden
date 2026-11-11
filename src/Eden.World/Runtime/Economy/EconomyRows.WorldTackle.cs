using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldTackle(
    long ModuleId,
    double CapacitorNeed,
    double DurationMilliseconds,
    Eden.Gameplay.Inventory.DogmaEffect Effect,
    long ExpiresAt,
    double MwdBlock,
    long PilotId,
    double Range,
    long SourceShipId,
    long StartedAt,
    double Strength,
    int SystemId,
    long TargetId,
    Eden.Gameplay.Propulsion.TackleTargetKind TargetKind,
    int TypeId,
    double WebPercent
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldTackle(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldTackle>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldTackle
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldTackle.ModuleId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldTackle(
                        ModuleId,CapacitorNeed,DurationMilliseconds,Effect,ExpiresAt,MwdBlock,PilotId,Range,SourceShipId,
                        StartedAt,Strength,SystemId,TargetId,TargetKind,TypeId,WebPercent
                    )
                    SELECT
                        json_extract(value,'$.ModuleId'),json_extract(value,'$.CapacitorNeed'),
                        json_extract(value,'$.DurationMilliseconds'),json_extract(value,'$.Effect'),
                        json_extract(value,'$.ExpiresAt'),json_extract(value,'$.MwdBlock'),json_extract(value,'$.PilotId'),
                        json_extract(value,'$.Range'),json_extract(value,'$.SourceShipId'),
                        json_extract(value,'$.StartedAt'),json_extract(value,'$.Strength'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.TargetId'),json_extract(value,'$.TargetKind'),json_extract(value,'$.TypeId'),
                        json_extract(value,'$.WebPercent')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ModuleId) DO UPDATE SET
                        CapacitorNeed=excluded.CapacitorNeed,DurationMilliseconds=excluded.DurationMilliseconds,
                        Effect=excluded.Effect,ExpiresAt=excluded.ExpiresAt,MwdBlock=excluded.MwdBlock,
                        PilotId=excluded.PilotId,Range=excluded.Range,SourceShipId=excluded.SourceShipId,
                        StartedAt=excluded.StartedAt,Strength=excluded.Strength,SystemId=excluded.SystemId,
                        TargetId=excluded.TargetId,TargetKind=excluded.TargetKind,TypeId=excluded.TypeId,
                        WebPercent=excluded.WebPercent
                    """
                );
            }
        }
    }
}
