using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldCombatAnomaly(
    long Id,
    long? CompletedAt,
    int DefinitionVersion,
    int DungeonId,
    bool IsSignature,
    long? NextWaveAt,
    long? ReplaceAt,
    int Slot,
    int SystemId,
    int Wave,
    double X,
    double Y,
    double Z,
    long DedVisitedRooms,
    long? EscalationPilotId,
    int EscalationSourceDungeonId,
    long? EscalationExpiresAt
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldCombatAnomaly(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldCombatAnomaly>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCombatAnomaly
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCombatAnomaly.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCombatAnomaly(
                        Id,CompletedAt,DefinitionVersion,DungeonId,IsSignature,NextWaveAt,ReplaceAt,Slot,SystemId,Wave,X,Y,Z,
                        DedVisitedRooms,EscalationPilotId,EscalationSourceDungeonId,EscalationExpiresAt
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.CompletedAt'),
                        json_extract(value,'$.DefinitionVersion'),json_extract(value,'$.DungeonId'),
                        json_extract(value,'$.IsSignature'),json_extract(value,'$.NextWaveAt'),
                        json_extract(value,'$.ReplaceAt'),json_extract(value,'$.Slot'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.Wave'),json_extract(value,'$.X'),json_extract(value,'$.Y'),
                        json_extract(value,'$.Z'),json_extract(value,'$.DedVisitedRooms'),json_extract(value,'$.EscalationPilotId'),
                        json_extract(value,'$.EscalationSourceDungeonId'),json_extract(value,'$.EscalationExpiresAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        CompletedAt=excluded.CompletedAt,DefinitionVersion=excluded.DefinitionVersion,
                        DungeonId=excluded.DungeonId,IsSignature=excluded.IsSignature,NextWaveAt=excluded.NextWaveAt,
                        ReplaceAt=excluded.ReplaceAt,Slot=excluded.Slot,SystemId=excluded.SystemId,Wave=excluded.Wave,
                        X=excluded.X,Y=excluded.Y,Z=excluded.Z,DedVisitedRooms=excluded.DedVisitedRooms,
                        EscalationPilotId=excluded.EscalationPilotId,EscalationSourceDungeonId=excluded.EscalationSourceDungeonId,
                        EscalationExpiresAt=excluded.EscalationExpiresAt
                    """
                );
            }
        }
    }
}
