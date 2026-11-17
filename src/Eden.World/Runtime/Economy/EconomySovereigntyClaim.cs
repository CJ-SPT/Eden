using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyClaim(int SystemId, long HubId, int CorporationId, int AllianceId, long ClaimedAt);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyClaim() =>
        Register(
            new EconomyTable<SavedSovereigntyClaim, EconomySovereigntyClaim>(
                "WorldSovereigntyClaim",
                row => new(row.SystemId),
                row => new(row.SystemId, row.HubId, row.CorporationId, row.AllianceId, row.ClaimedAt),
                state => new SavedSovereigntyClaim
                {
                    SystemId = state.SystemId,
                    HubId = state.HubId,
                    CorporationId = state.CorporationId,
                    AllianceId = state.AllianceId,
                    ClaimedAt = state.ClaimedAt,
                },
                ProjectSovereigntyClaim,
                "SystemId,HubId,CorporationId,AllianceId,ClaimedAt",
                reader => new(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt64(5)),
                state => new(state.SystemId),
                indexes: [new("AllianceId", row => row.AllianceId)],
                primaryColumn: "SystemId"
            )
        );

    private static void ProjectSovereigntyClaim(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySovereigntyClaim>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyClaim WHERE SystemId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyClaim(SystemId,HubId,CorporationId,AllianceId,ClaimedAt)
                SELECT
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.HubId'),
                    json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.ClaimedAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SystemId) DO UPDATE SET
                    HubId=excluded.HubId,
                    CorporationId=excluded.CorporationId,
                    AllianceId=excluded.AllianceId,
                    ClaimedAt=excluded.ClaimedAt
                """
            );
        }
    }
}
