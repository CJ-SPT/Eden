using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCombatDamage(long VictimId, long AttackerId, double Damage, string PartyJson);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCombatDamage(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCombatDamage>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCombatDamage
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCombatDamage.VictimId=json_extract(value,'$.Id') AND
                        WorldCombatDamage.AttackerId=json_extract(value,'$.Part')
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
                    INSERT INTO WorldCombatDamage(
                        VictimId,AttackerId,Damage,PartyJson
                    )
                    SELECT
                        json_extract(value,'$.VictimId'),json_extract(value,'$.AttackerId'),json_extract(value,'$.Damage'),
                        json_extract(value,'$.PartyJson')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(VictimId,AttackerId) DO UPDATE SET
                        Damage=excluded.Damage,PartyJson=excluded.PartyJson
                    """
                );
            }
        }
    }
}
