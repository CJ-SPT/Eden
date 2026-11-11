using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldDroneCombatTriggers(
    long ShipId,
    long IncomingAt,
    long IncomingRevision,
    long IncomingSourceId,
    long OffensiveAt,
    long OffensiveRevision,
    long OffensiveTargetId
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldDroneCombatTriggers(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyWorldDroneCombatTriggers>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldDroneCombatTriggers
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldDroneCombatTriggers.ShipId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldDroneCombatTriggers(
                        ShipId,IncomingAt,IncomingRevision,IncomingSourceId,OffensiveAt,OffensiveRevision,OffensiveTargetId
                    )
                    SELECT
                        json_extract(value,'$.ShipId'),json_extract(value,'$.IncomingAt'),
                        json_extract(value,'$.IncomingRevision'),json_extract(value,'$.IncomingSourceId'),
                        json_extract(value,'$.OffensiveAt'),json_extract(value,'$.OffensiveRevision'),
                        json_extract(value,'$.OffensiveTargetId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ShipId) DO UPDATE SET
                        IncomingAt=excluded.IncomingAt,IncomingRevision=excluded.IncomingRevision,
                        IncomingSourceId=excluded.IncomingSourceId,OffensiveAt=excluded.OffensiveAt,
                        OffensiveRevision=excluded.OffensiveRevision,OffensiveTargetId=excluded.OffensiveTargetId
                    """
                );
            }
        }
    }
}
