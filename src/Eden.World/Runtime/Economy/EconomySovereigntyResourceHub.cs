using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyResourceHub(long HubId, int SystemId, long UpdatedAt, long Power, long Workforce);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyResourceHub() =>
        Register(
            new EconomyTable<SavedSovereigntyResourceHub, EconomySovereigntyResourceHub>(
                "WorldSovereigntyResourceHub",
                row => new(row.HubId),
                row => new(row.HubId, row.SystemId, row.UpdatedAt, row.Power, row.Workforce),
                state => new SavedSovereigntyResourceHub
                {
                    HubId = state.HubId,
                    SystemId = state.SystemId,
                    UpdatedAt = state.UpdatedAt,
                    Power = state.Power,
                    Workforce = state.Workforce,
                },
                ProjectSovereigntyResourceHub,
                "HubId,SystemId,UpdatedAt,Power,Workforce",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)),
                state => new(state.HubId),
                indexes: [new("SystemId", row => row.SystemId)],
                primaryColumn: "HubId"
            )
        );

    private static void ProjectSovereigntyResourceHub(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySovereigntyResourceHub>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyResourceHub WHERE HubId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyResourceHub(HubId,SystemId,UpdatedAt,Power,Workforce)
                SELECT
                    json_extract(value,'$.HubId'),
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.UpdatedAt'),
                    json_extract(value,'$.Power'),
                    json_extract(value,'$.Workforce')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(HubId) DO UPDATE SET
                    SystemId=excluded.SystemId,
                    UpdatedAt=excluded.UpdatedAt,
                    Power=excluded.Power,
                    Workforce=excluded.Workforce
                """
            );
        }
    }
}
