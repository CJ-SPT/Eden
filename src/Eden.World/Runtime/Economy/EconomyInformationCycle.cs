using System.Text.Json;
using Eden.Data.World.Information;
using Eden.Gameplay.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyInformationCycle(
    long ModuleId,
    long PilotId,
    long ShipId,
    long TargetId,
    DogmaEffect Effect,
    long StartedAt,
    long ReadyAt,
    string ModuleJson
);

internal sealed partial class ResidentEconomy
{
    private void RegisterInformationCycles()
    {
        Register(
            new EconomyTable<SavedInformationCycle, EconomyInformationCycle>(
                "WorldInformationCycle",
                row => new(row.ModuleId),
                row => new(row.ModuleId, row.PilotId, row.ShipId, row.TargetId, row.Effect, row.StartedAt, row.ReadyAt, row.ModuleJson),
                state => new SavedInformationCycle
                {
                    ModuleId = state.ModuleId,
                    PilotId = state.PilotId,
                    ShipId = state.ShipId,
                    TargetId = state.TargetId,
                    Effect = state.Effect,
                    StartedAt = state.StartedAt,
                    ReadyAt = state.ReadyAt,
                    ModuleJson = state.ModuleJson,
                },
                ProjectInformationCycles,
                "ModuleId,PilotId,ShipId,TargetId,Effect,StartedAt,ReadyAt,ModuleJson",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        (DogmaEffect)reader.GetInt32(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetString(8)
                    ),
                state => new(state.ModuleId),
                indexes: [new("ShipId", row => row.ShipId), new("TargetId", row => row.TargetId)],
                primaryColumn: "ModuleId"
            )
        );
    }

    private static void ProjectInformationCycles(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyInformationCycle>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldInformationCycle WHERE ModuleId IN (SELECT value FROM json_each({removed}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldInformationCycle(ModuleId,PilotId,ShipId,TargetId,Effect,StartedAt,ReadyAt,ModuleJson)
                SELECT json_extract(value,'$.ModuleId'),json_extract(value,'$.PilotId'),json_extract(value,'$.ShipId'),
                    json_extract(value,'$.TargetId'),json_extract(value,'$.Effect'),json_extract(value,'$.StartedAt'),
                    json_extract(value,'$.ReadyAt'),json_extract(value,'$.ModuleJson')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(ModuleId) DO UPDATE SET PilotId=excluded.PilotId,ShipId=excluded.ShipId,TargetId=excluded.TargetId,
                    Effect=excluded.Effect,StartedAt=excluded.StartedAt,ReadyAt=excluded.ReadyAt,ModuleJson=excluded.ModuleJson
                """
            );
        }
    }
}
