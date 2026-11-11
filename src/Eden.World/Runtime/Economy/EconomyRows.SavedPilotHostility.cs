using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedPilotHostility(long SourceShipId, long TargetShipId, long ExpiresAt, long LastAttackAt);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedPilotHostility(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedPilotHostility>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldPilotHostility
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldPilotHostility.SourceShipId=json_extract(value,'$.Id') AND
                        WorldPilotHostility.TargetShipId=json_extract(value,'$.Part')
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
                    INSERT INTO WorldPilotHostility(
                        SourceShipId,TargetShipId,ExpiresAt,LastAttackAt
                    )
                    SELECT
                        json_extract(value,'$.SourceShipId'),json_extract(value,'$.TargetShipId'),
                        json_extract(value,'$.ExpiresAt'),json_extract(value,'$.LastAttackAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(SourceShipId,TargetShipId) DO UPDATE SET
                        ExpiresAt=excluded.ExpiresAt,LastAttackAt=excluded.LastAttackAt
                    """
                );
            }
        }
    }
}
