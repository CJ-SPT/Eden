using System.Text.Json;
using Eden.Data.World.Structures;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyStructureEnergy(long StructureId, double Charge, long UpdatedAt);

internal sealed partial class ResidentEconomy
{
    private void RegisterStructureEnergy() =>
        Register(
            new EconomyTable<SavedStructureEnergy, EconomyStructureEnergy>(
                "WorldStructureEnergy",
                row => new(row.StructureId),
                row => new(row.StructureId, row.Charge, row.UpdatedAt),
                state =>
                    new()
                    {
                        StructureId = state.StructureId,
                        Charge = state.Charge,
                        UpdatedAt = state.UpdatedAt,
                    },
                ProjectStructureEnergy,
                "StructureId,Charge,UpdatedAt",
                reader => new(reader.GetInt64(1), reader.GetDouble(2), reader.GetInt64(3)),
                state => new(state.StructureId),
                primaryColumn: "StructureId"
            )
        );

    private static void ProjectStructureEnergy(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyStructureEnergy>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldStructureEnergy WHERE StructureId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldStructureEnergy(StructureId,Charge,UpdatedAt)
                SELECT json_extract(value,'$.StructureId'),json_extract(value,'$.Charge'),json_extract(value,'$.UpdatedAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(StructureId) DO UPDATE SET Charge=excluded.Charge,UpdatedAt=excluded.UpdatedAt
                """
            );
        }
    }
}
