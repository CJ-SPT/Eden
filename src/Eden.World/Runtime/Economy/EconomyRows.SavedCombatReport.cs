using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCombatReport(long Id, string Data, bool IsLoss, long PilotId, string SourceKey, long Time);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCombatReport(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCombatReport>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCombatReport
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCombatReport.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCombatReport(
                        Id,Data,IsLoss,PilotId,SourceKey,Time
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.Data'),json_extract(value,'$.IsLoss'),
                        json_extract(value,'$.PilotId'),json_extract(value,'$.SourceKey'),json_extract(value,'$.Time')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        Data=excluded.Data,IsLoss=excluded.IsLoss,PilotId=excluded.PilotId,SourceKey=excluded.SourceKey,
                        Time=excluded.Time
                    """
                );
            }
        }
    }
}
