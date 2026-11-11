using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldPlayerEwar(
    long ModuleId,
    string ContactJson,
    long PilotId,
    long SourceShipId,
    int SystemId,
    long TargetId,
    Eden.Gameplay.Propulsion.TackleTargetKind TargetKind
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldPlayerEwar(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldPlayerEwar>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldPlayerEwar
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldPlayerEwar.ModuleId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldPlayerEwar(
                        ModuleId,ContactJson,PilotId,SourceShipId,SystemId,TargetId,TargetKind
                    )
                    SELECT
                        json_extract(value,'$.ModuleId'),json_extract(value,'$.ContactJson'),
                        json_extract(value,'$.PilotId'),json_extract(value,'$.SourceShipId'),
                        json_extract(value,'$.SystemId'),json_extract(value,'$.TargetId'),json_extract(value,'$.TargetKind')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ModuleId) DO UPDATE SET
                        ContactJson=excluded.ContactJson,PilotId=excluded.PilotId,SourceShipId=excluded.SourceShipId,
                        SystemId=excluded.SystemId,TargetId=excluded.TargetId,TargetKind=excluded.TargetKind
                    """
                );
            }
        }
    }
}
