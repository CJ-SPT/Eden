using System.Text.Json;
using Eden.Data.World.Structures;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyStructureBattle(
    long StructureId,
    double Shield,
    double Armor,
    double Hull,
    long? RepairAt,
    long DamageWindowAt,
    double ShieldWindow,
    double ArmorWindow,
    double HullWindow,
    long PausedUntil,
    double ShieldCapacity,
    double ArmorCapacity,
    double HullCapacity
);

internal sealed partial class ResidentEconomy
{
    private void RegisterStructureBattle() =>
        Register(
            new EconomyTable<SavedStructureBattle, EconomyStructureBattle>(
                "WorldStructureBattle",
                row => new(row.StructureId),
                row =>
                    new(
                        row.StructureId,
                        row.Shield,
                        row.Armor,
                        row.Hull,
                        row.RepairAt,
                        row.DamageWindowAt,
                        row.ShieldWindow,
                        row.ArmorWindow,
                        row.HullWindow,
                        row.PausedUntil,
                        row.ShieldCapacity,
                        row.ArmorCapacity,
                        row.HullCapacity
                    ),
                state => new SavedStructureBattle
                {
                    StructureId = state.StructureId,
                    Shield = state.Shield,
                    Armor = state.Armor,
                    Hull = state.Hull,
                    RepairAt = state.RepairAt,
                    DamageWindowAt = state.DamageWindowAt,
                    ShieldWindow = state.ShieldWindow,
                    ArmorWindow = state.ArmorWindow,
                    HullWindow = state.HullWindow,
                    PausedUntil = state.PausedUntil,
                    ShieldCapacity = state.ShieldCapacity,
                    ArmorCapacity = state.ArmorCapacity,
                    HullCapacity = state.HullCapacity,
                },
                ProjectStructureBattle,
                "StructureId,Shield,Armor,Hull,RepairAt,DamageWindowAt,ShieldWindow,ArmorWindow,HullWindow,PausedUntil,"
                    + "ShieldCapacity,ArmorCapacity,HullCapacity",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetDouble(2),
                        reader.GetDouble(3),
                        reader.GetDouble(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetDouble(7),
                        reader.GetDouble(8),
                        reader.GetDouble(9),
                        reader.GetInt64(10),
                        reader.GetDouble(11),
                        reader.GetDouble(12),
                        reader.GetDouble(13)
                    ),
                state => new(state.StructureId),
                indexes: [new("StructureId", row => row.StructureId)],
                primaryColumn: "StructureId"
            )
        );

    private static void ProjectStructureBattle(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyStructureBattle>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldStructureBattle WHERE StructureId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldStructureBattle(
                    StructureId,Shield,Armor,Hull,RepairAt,DamageWindowAt,ShieldWindow,ArmorWindow,HullWindow,PausedUntil,
                    ShieldCapacity,ArmorCapacity,HullCapacity
                )
                SELECT
                    json_extract(value,'$.StructureId'),
                    json_extract(value,'$.Shield'),
                    json_extract(value,'$.Armor'),
                    json_extract(value,'$.Hull'),
                    json_extract(value,'$.RepairAt'),
                    json_extract(value,'$.DamageWindowAt'),
                    json_extract(value,'$.ShieldWindow'),
                    json_extract(value,'$.ArmorWindow'),
                    json_extract(value,'$.HullWindow'),
                    json_extract(value,'$.PausedUntil'),
                    json_extract(value,'$.ShieldCapacity'),
                    json_extract(value,'$.ArmorCapacity'),
                    json_extract(value,'$.HullCapacity')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(StructureId) DO UPDATE SET
                    Shield=excluded.Shield,
                    Armor=excluded.Armor,
                    Hull=excluded.Hull,
                    RepairAt=excluded.RepairAt,
                    DamageWindowAt=excluded.DamageWindowAt,
                    ShieldWindow=excluded.ShieldWindow,
                    ArmorWindow=excluded.ArmorWindow,
                    HullWindow=excluded.HullWindow,
                    PausedUntil=excluded.PausedUntil,
                    ShieldCapacity=excluded.ShieldCapacity,
                    ArmorCapacity=excluded.ArmorCapacity,
                    HullCapacity=excluded.HullCapacity
                """
            );
        }
    }
}
