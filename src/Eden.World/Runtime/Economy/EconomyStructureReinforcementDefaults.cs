using System.Text.Json;
using Eden.Data.World.Structures;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyStructureReinforcementDefault(int CorporationId, int Hour);

internal sealed partial class ResidentEconomy
{
    private void RegisterStructureReinforcementDefaults() =>
        Register(
            new EconomyTable<SavedStructureReinforcementDefault, EconomyStructureReinforcementDefault>(
                "WorldStructureReinforcementDefault",
                row => new(row.CorporationId),
                row => new(row.CorporationId, row.Hour),
                state => new SavedStructureReinforcementDefault { CorporationId = state.CorporationId, Hour = state.Hour },
                ProjectStructureReinforcementDefaults,
                "CorporationId,Hour",
                reader => new(reader.GetInt32(1), reader.GetInt32(2)),
                state => new(state.CorporationId),
                primaryColumn: nameof(SavedStructureReinforcementDefault.CorporationId),
                rowIdIsKey: true
            )
        );

    private static void ProjectStructureReinforcementDefaults(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyStructureReinforcementDefault>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"DELETE FROM WorldStructureReinforcementDefault WHERE CorporationId IN (SELECT value FROM json_each({removed}))"
            );
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldStructureReinforcementDefault(CorporationId,Hour)
                SELECT json_extract(value,'$.CorporationId'),json_extract(value,'$.Hour') FROM json_each({rows}) WHERE 1
                ON CONFLICT(CorporationId) DO UPDATE SET Hour=excluded.Hour
                """
            );
        }
    }
}
