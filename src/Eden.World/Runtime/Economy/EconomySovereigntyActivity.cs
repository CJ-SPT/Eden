using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyActivity(
    int SystemId,
    double MilitaryPoints,
    double IndustrialPoints,
    long UpdatedAt,
    long LastMilitaryAt,
    long LastIndustrialAt,
    long Revision
);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyActivity() =>
        Register(
            new EconomyTable<SavedSovereigntyActivity, EconomySovereigntyActivity>(
                "WorldSovereigntyActivity",
                row => new(row.SystemId),
                row =>
                    new(
                        row.SystemId,
                        row.MilitaryPoints,
                        row.IndustrialPoints,
                        row.UpdatedAt,
                        row.LastMilitaryAt,
                        row.LastIndustrialAt,
                        row.Revision
                    ),
                state => new SavedSovereigntyActivity
                {
                    SystemId = state.SystemId,
                    MilitaryPoints = state.MilitaryPoints,
                    IndustrialPoints = state.IndustrialPoints,
                    UpdatedAt = state.UpdatedAt,
                    LastMilitaryAt = state.LastMilitaryAt,
                    LastIndustrialAt = state.LastIndustrialAt,
                    Revision = state.Revision,
                },
                ProjectSovereigntyActivity,
                "SystemId,MilitaryPoints,IndustrialPoints,UpdatedAt,LastMilitaryAt,LastIndustrialAt,Revision",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.GetDouble(2),
                        reader.GetDouble(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7)
                    ),
                state => new(state.SystemId),
                indexes: [],
                primaryColumn: "SystemId"
            )
        );

    private static void ProjectSovereigntyActivity(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySovereigntyActivity>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyActivity WHERE SystemId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyActivity(
                    SystemId,
                    MilitaryPoints,
                    IndustrialPoints,
                    UpdatedAt,
                    LastMilitaryAt,
                    LastIndustrialAt,
                    Revision)
                SELECT
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.MilitaryPoints'),
                    json_extract(value,'$.IndustrialPoints'),
                    json_extract(value,'$.UpdatedAt'),
                    json_extract(value,'$.LastMilitaryAt'),
                    json_extract(value,'$.LastIndustrialAt'),
                    json_extract(value,'$.Revision')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SystemId) DO UPDATE SET
                    MilitaryPoints=excluded.MilitaryPoints,
                    IndustrialPoints=excluded.IndustrialPoints,
                    UpdatedAt=excluded.UpdatedAt,
                    LastMilitaryAt=excluded.LastMilitaryAt,
                    LastIndustrialAt=excluded.LastIndustrialAt,
                    Revision=excluded.Revision
                """
            );
        }
    }
}
