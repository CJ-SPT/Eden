using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyPendingSecurityReward(
    long PilotId,
    int CorporationId,
    long Due,
    double Fraction,
    double Maximum,
    long NpcId,
    int SystemId,
    int TypeId
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectPendingSecurityReward(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyPendingSecurityReward>> changes
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
                    DELETE FROM WorldSecurityPending
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldSecurityPending.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldSecurityPending(
                        PilotId,CorporationId,Due,Fraction,Maximum,NpcId,SystemId,TypeId
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.CorporationId'),json_extract(value,'$.Due'),
                        json_extract(value,'$.Fraction'),json_extract(value,'$.Maximum'),json_extract(value,'$.NpcId'),
                        json_extract(value,'$.SystemId'),json_extract(value,'$.TypeId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        CorporationId=excluded.CorporationId,Due=excluded.Due,Fraction=excluded.Fraction,
                        Maximum=excluded.Maximum,NpcId=excluded.NpcId,SystemId=excluded.SystemId,TypeId=excluded.TypeId
                    """
                );
            }
        }
    }
}
