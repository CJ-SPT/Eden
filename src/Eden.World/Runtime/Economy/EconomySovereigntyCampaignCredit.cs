using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyCampaignCredit(long HubId, int AllianceId, int SystemId, long? VictoryAt);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyCampaignCredit() =>
        Register(
            new EconomyTable<SavedSovereigntyCampaignCredit, EconomySovereigntyCampaignCredit>(
                "WorldSovereigntyCampaignCredit",
                row => new(row.HubId, row.AllianceId),
                row => new(row.HubId, row.AllianceId, row.SystemId, row.VictoryAt),
                state => new SavedSovereigntyCampaignCredit
                {
                    HubId = state.HubId,
                    AllianceId = state.AllianceId,
                    SystemId = state.SystemId,
                    VictoryAt = state.VictoryAt,
                },
                ProjectSovereigntyCampaignCredit,
                "HubId,AllianceId,SystemId,VictoryAt",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt64(4)),
                state => new(state.HubId, state.AllianceId),
                indexes: [new("HubId", row => row.HubId), new("AllianceId", row => row.AllianceId), new("SystemId", row => row.SystemId)],
                primaryColumn: null
            )
        );

    private static void ProjectSovereigntyCampaignCredit(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySovereigntyCampaignCredit>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldSovereigntyCampaignCredit WHERE (HubId,AllianceId) IN (
                    SELECT json_extract(value,'$.Id'),json_extract(value,'$.Part') FROM json_each({removed}));
                """
            );
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyCampaignCredit(HubId,AllianceId,SystemId,VictoryAt)
                SELECT json_extract(value,'$.HubId'),json_extract(value,'$.AllianceId'),
                       json_extract(value,'$.SystemId'),json_extract(value,'$.VictoryAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(HubId,AllianceId) DO UPDATE SET VictoryAt=excluded.VictoryAt;
                """
            );
        }
    }
}
