using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldDroneSettings(long PilotId, bool Aggressive, bool FocusFire);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldDroneSettings(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldDroneSettings>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldDroneSettings
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldDroneSettings.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldDroneSettings(
                        PilotId,Aggressive,FocusFire
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.Aggressive'),json_extract(value,'$.FocusFire')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        Aggressive=excluded.Aggressive,FocusFire=excluded.FocusFire
                    """
                );
            }
        }
    }
}
