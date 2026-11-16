using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyEntosisCycle(
    long ModuleId,
    int TypeId,
    long PilotId,
    long ShipId,
    long HubId,
    int SystemId,
    int CorporationId,
    int AllianceId,
    long StartedAt,
    long EndsAt,
    long WarmupUntil,
    double Range,
    double SpeedLimit,
    bool Capital,
    long Repeats,
    bool Stopping,
    bool Contributing
);

internal sealed partial class ResidentEconomy
{
    private void RegisterEntosisCycle() =>
        Register(
            new EconomyTable<SavedEntosisCycle, EconomyEntosisCycle>(
                "WorldEntosisCycle",
                row => new(row.ModuleId),
                row =>
                    new(
                        row.ModuleId,
                        row.TypeId,
                        row.PilotId,
                        row.ShipId,
                        row.HubId,
                        row.SystemId,
                        row.CorporationId,
                        row.AllianceId,
                        row.StartedAt,
                        row.EndsAt,
                        row.WarmupUntil,
                        row.Range,
                        row.SpeedLimit,
                        row.Capital,
                        row.Repeats,
                        row.Stopping,
                        row.Contributing
                    ),
                state => new SavedEntosisCycle
                {
                    ModuleId = state.ModuleId,
                    TypeId = state.TypeId,
                    PilotId = state.PilotId,
                    ShipId = state.ShipId,
                    HubId = state.HubId,
                    SystemId = state.SystemId,
                    CorporationId = state.CorporationId,
                    AllianceId = state.AllianceId,
                    StartedAt = state.StartedAt,
                    EndsAt = state.EndsAt,
                    WarmupUntil = state.WarmupUntil,
                    Range = state.Range,
                    SpeedLimit = state.SpeedLimit,
                    Capital = state.Capital,
                    Repeats = state.Repeats,
                    Stopping = state.Stopping,
                    Contributing = state.Contributing,
                },
                ProjectEntosisCycle,
                "ModuleId,TypeId,PilotId,ShipId,HubId,SystemId,CorporationId,AllianceId,StartedAt,EndsAt,"
                    + "WarmupUntil,Range,SpeedLimit,Capital,Repeats,Stopping,Contributing",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        reader.GetInt64(11),
                        reader.GetDouble(12),
                        reader.GetDouble(13),
                        reader.GetBoolean(14),
                        reader.GetInt64(15),
                        reader.GetBoolean(16),
                        reader.GetBoolean(17)
                    ),
                state => new(state.ModuleId),
                indexes: [new("PilotId", row => row.PilotId), new("ShipId", row => row.ShipId), new("SystemId", row => row.SystemId)],
                primaryColumn: "ModuleId"
            )
        );

    private static void ProjectEntosisCycle(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyEntosisCycle>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldEntosisCycle WHERE ModuleId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldEntosisCycle(
                    ModuleId,TypeId,PilotId,ShipId,HubId,SystemId,CorporationId,AllianceId,StartedAt,EndsAt,
                    WarmupUntil,Range,SpeedLimit,Capital,Repeats,Stopping,Contributing)
                SELECT
                    json_extract(value,'$.ModuleId'),
                    json_extract(value,'$.TypeId'),
                    json_extract(value,'$.PilotId'),
                    json_extract(value,'$.ShipId'),
                    json_extract(value,'$.HubId'),
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.StartedAt'),
                    json_extract(value,'$.EndsAt'),
                    json_extract(value,'$.WarmupUntil'),
                    json_extract(value,'$.Range'),
                    json_extract(value,'$.SpeedLimit'),
                    json_extract(value,'$.Capital'),
                    json_extract(value,'$.Repeats'),
                    json_extract(value,'$.Stopping'),
                    json_extract(value,'$.Contributing')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(ModuleId) DO UPDATE SET
                    TypeId=excluded.TypeId,
                    PilotId=excluded.PilotId,
                    ShipId=excluded.ShipId,
                    HubId=excluded.HubId,
                    SystemId=excluded.SystemId,
                    CorporationId=excluded.CorporationId,
                    AllianceId=excluded.AllianceId,
                    StartedAt=excluded.StartedAt,
                    EndsAt=excluded.EndsAt,
                    WarmupUntil=excluded.WarmupUntil,
                    Range=excluded.Range,
                    SpeedLimit=excluded.SpeedLimit,
                    Capital=excluded.Capital,
                    Repeats=excluded.Repeats,
                    Stopping=excluded.Stopping,
                    Contributing=excluded.Contributing
                """
            );
        }
    }
}
