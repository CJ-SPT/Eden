using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyAllianceApplication(
    long Id,
    int CorporationId,
    int AllianceId,
    string Text,
    Eden.Gameplay.Alliances.AllianceApplicationStatus Status,
    long AppliedAt,
    long? EffectiveAt
);

internal sealed partial class ResidentEconomy
{
    private void RegisterAllianceApplication() =>
        Register(
            new EconomyTable<SavedAllianceApplication, EconomyAllianceApplication>(
                "WorldAllianceApplication",
                row => new(row.Id),
                row => new(row.Id, row.CorporationId, row.AllianceId, row.Text, row.Status, row.AppliedAt, row.EffectiveAt),
                state => new SavedAllianceApplication
                {
                    Id = state.Id,
                    CorporationId = state.CorporationId,
                    AllianceId = state.AllianceId,
                    Text = state.Text,
                    Status = state.Status,
                    AppliedAt = state.AppliedAt,
                    EffectiveAt = state.EffectiveAt,
                },
                ProjectAllianceApplication,
                "Id,CorporationId,AllianceId,Text,Status,AppliedAt,EffectiveAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetString(4),
                        (Eden.Gameplay.Alliances.AllianceApplicationStatus)reader.GetInt32(5),
                        reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("CorporationId", row => row.CorporationId),
                    new("AllianceId", row => row.AllianceId),
                    new("Status", row => row.Status),
                ],
                primaryColumn: "Id"
            )
        );

    private static void ProjectAllianceApplication(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAllianceApplication>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAllianceApplication WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAllianceApplication(Id,CorporationId,AllianceId,Text,Status,AppliedAt,EffectiveAt)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.Text'),
                    json_extract(value,'$.Status'),
                    json_extract(value,'$.AppliedAt'),
                    json_extract(value,'$.EffectiveAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    CorporationId=excluded.CorporationId,
                    AllianceId=excluded.AllianceId,
                    Text=excluded.Text,
                    Status=excluded.Status,
                    AppliedAt=excluded.AppliedAt,
                    EffectiveAt=excluded.EffectiveAt
                """
            );
        }
    }
}
