using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyAllianceMember(int CorporationId, int AllianceId, long JoinedAt, int? ChosenExecutorId);

internal sealed partial class ResidentEconomy
{
    private void RegisterAllianceMember() =>
        Register(
            new EconomyTable<SavedAllianceMember, EconomyAllianceMember>(
                "WorldAllianceMember",
                row => new(row.CorporationId),
                row => new(row.CorporationId, row.AllianceId, row.JoinedAt, row.ChosenExecutorId),
                state => new SavedAllianceMember
                {
                    CorporationId = state.CorporationId,
                    AllianceId = state.AllianceId,
                    JoinedAt = state.JoinedAt,
                    ChosenExecutorId = state.ChosenExecutorId,
                },
                ProjectAllianceMember,
                "CorporationId,AllianceId,JoinedAt,ChosenExecutorId",
                reader => new(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt32(4)),
                state => new(state.CorporationId),
                indexes: [new("AllianceId", row => row.AllianceId)],
                primaryColumn: "CorporationId"
            )
        );

    private static void ProjectAllianceMember(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAllianceMember>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAllianceMember WHERE CorporationId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAllianceMember(CorporationId,AllianceId,JoinedAt,ChosenExecutorId)
                SELECT
                    json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.JoinedAt'),
                    json_extract(value,'$.ChosenExecutorId')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(CorporationId) DO UPDATE SET
                    AllianceId=excluded.AllianceId,
                    JoinedAt=excluded.JoinedAt,
                    ChosenExecutorId=excluded.ChosenExecutorId
                """
            );
        }
    }
}
