using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedNaniteRepair(
    long ModuleId,
    long CompletesAt,
    System.Guid Generation,
    double InitialDamage,
    double InitialHitPoints,
    long PasteUnits,
    double PasteUnitsPerDamage,
    long PilotId,
    long ShipId,
    long StartedAt
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedNaniteRepair(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedNaniteRepair>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldNaniteRepair
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldNaniteRepair.ModuleId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldNaniteRepair(
                        ModuleId,CompletesAt,Generation,InitialDamage,InitialHitPoints,PasteUnits,PasteUnitsPerDamage,
                        PilotId,ShipId,StartedAt
                    )
                    SELECT
                        json_extract(value,'$.ModuleId'),json_extract(value,'$.CompletesAt'),
                        json_extract(value,'$.Generation'),json_extract(value,'$.InitialDamage'),
                        json_extract(value,'$.InitialHitPoints'),json_extract(value,'$.PasteUnits'),
                        json_extract(value,'$.PasteUnitsPerDamage'),json_extract(value,'$.PilotId'),
                        json_extract(value,'$.ShipId'),json_extract(value,'$.StartedAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ModuleId) DO UPDATE SET
                        CompletesAt=excluded.CompletesAt,Generation=excluded.Generation,
                        InitialDamage=excluded.InitialDamage,InitialHitPoints=excluded.InitialHitPoints,
                        PasteUnits=excluded.PasteUnits,PasteUnitsPerDamage=excluded.PasteUnitsPerDamage,
                        PilotId=excluded.PilotId,ShipId=excluded.ShipId,StartedAt=excluded.StartedAt
                    """
                );
            }
        }
    }
}
