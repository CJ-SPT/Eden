using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldAnomalyMember(long NpcId, bool Sentry, long SiteId, bool Trigger, int Wave);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldAnomalyMember(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldAnomalyMember>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldAnomalyMember
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldAnomalyMember.NpcId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldAnomalyMember(
                        NpcId,Sentry,SiteId,Trigger,Wave
                    )
                    SELECT
                        json_extract(value,'$.NpcId'),json_extract(value,'$.Sentry'),json_extract(value,'$.SiteId'),
                        json_extract(value,'$.Trigger'),json_extract(value,'$.Wave')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(NpcId) DO UPDATE SET
                        Sentry=excluded.Sentry,SiteId=excluded.SiteId,Trigger=excluded.Trigger,Wave=excluded.Wave
                    """
                );
            }
        }
    }
}
