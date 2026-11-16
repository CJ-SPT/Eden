using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyHub(
    int SystemId,
    long HubId,
    long CaptureDuration,
    int? ControllingAllianceId,
    long ProgressTicks,
    long UpdatedAt,
    int? ScoringAllianceId,
    int Rate,
    bool Blocked
);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyHub() =>
        Register(
            new EconomyTable<SavedSovereigntyHub, EconomySovereigntyHub>(
                "WorldSovereigntyHub",
                row => new(row.SystemId),
                row =>
                    new(
                        row.SystemId,
                        row.HubId,
                        row.CaptureDuration,
                        row.ControllingAllianceId,
                        row.ProgressTicks,
                        row.UpdatedAt,
                        row.ScoringAllianceId,
                        row.Rate,
                        row.Blocked
                    ),
                state => new SavedSovereigntyHub
                {
                    SystemId = state.SystemId,
                    HubId = state.HubId,
                    CaptureDuration = state.CaptureDuration,
                    ControllingAllianceId = state.ControllingAllianceId,
                    ProgressTicks = state.ProgressTicks,
                    UpdatedAt = state.UpdatedAt,
                    ScoringAllianceId = state.ScoringAllianceId,
                    Rate = state.Rate,
                    Blocked = state.Blocked,
                },
                ProjectSovereigntyHub,
                "SystemId,HubId,CaptureDuration,ControllingAllianceId,ProgressTicks,UpdatedAt,ScoringAllianceId,Rate,Blocked",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.GetBoolean(9)
                    ),
                state => new(state.SystemId),
                indexes: [new("HubId", row => row.HubId)],
                primaryColumn: "SystemId"
            )
        );

    private static void ProjectSovereigntyHub(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySovereigntyHub>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyHub WHERE SystemId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyHub(
                    SystemId,HubId,CaptureDuration,ControllingAllianceId,ProgressTicks,UpdatedAt,ScoringAllianceId,Rate,Blocked)
                SELECT
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.HubId'),
                    json_extract(value,'$.CaptureDuration'),
                    json_extract(value,'$.ControllingAllianceId'),
                    json_extract(value,'$.ProgressTicks'),
                    json_extract(value,'$.UpdatedAt'),
                    json_extract(value,'$.ScoringAllianceId'),
                    json_extract(value,'$.Rate'),
                    json_extract(value,'$.Blocked')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SystemId) DO UPDATE SET
                    HubId=excluded.HubId,
                    CaptureDuration=excluded.CaptureDuration,
                    ControllingAllianceId=excluded.ControllingAllianceId,
                    ProgressTicks=excluded.ProgressTicks,
                    UpdatedAt=excluded.UpdatedAt,
                    ScoringAllianceId=excluded.ScoringAllianceId,
                    Rate=excluded.Rate,
                    Blocked=excluded.Blocked
                """
            );
        }
    }
}
