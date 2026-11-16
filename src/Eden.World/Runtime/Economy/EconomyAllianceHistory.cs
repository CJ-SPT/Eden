using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyAllianceHistory(long Id, int CorporationId, int? AllianceId, long StartedAt);

internal sealed partial class ResidentEconomy
{
    private void RegisterAllianceHistory() =>
        Register(
            new EconomyTable<SavedAllianceHistory, EconomyAllianceHistory>(
                "WorldAllianceHistory",
                row => new(row.Id),
                row => new(row.Id, row.CorporationId, row.AllianceId, row.StartedAt),
                state => new SavedAllianceHistory
                {
                    Id = state.Id,
                    CorporationId = state.CorporationId,
                    AllianceId = state.AllianceId,
                    StartedAt = state.StartedAt,
                },
                ProjectAllianceHistory,
                "Id,CorporationId,AllianceId,StartedAt",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetInt64(4)),
                state => new(state.Id),
                indexes: [new("CorporationId", row => row.CorporationId)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectAllianceHistory(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAllianceHistory>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAllianceHistory WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAllianceHistory(Id,CorporationId,AllianceId,StartedAt)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.StartedAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    CorporationId=excluded.CorporationId,
                    AllianceId=excluded.AllianceId,
                    StartedAt=excluded.StartedAt
                """
            );
        }
    }
}
