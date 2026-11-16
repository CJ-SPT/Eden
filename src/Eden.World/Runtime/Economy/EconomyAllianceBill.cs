using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyAllianceBill(
    long Id,
    int AllianceId,
    long IssuedAt,
    long DueAt,
    long AmountCents,
    int CorporationCount,
    long? PaidAt,
    int? PaidByCorporationId,
    int DivisionKey,
    bool Renewed
);

internal sealed partial class ResidentEconomy
{
    private void RegisterAllianceBill() =>
        Register(
            new EconomyTable<SavedAllianceBill, EconomyAllianceBill>(
                "WorldAllianceBill",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.AllianceId,
                        row.IssuedAt,
                        row.DueAt,
                        row.AmountCents,
                        row.CorporationCount,
                        row.PaidAt,
                        row.PaidByCorporationId,
                        row.DivisionKey,
                        row.Renewed
                    ),
                state => new SavedAllianceBill
                {
                    Id = state.Id,
                    AllianceId = state.AllianceId,
                    IssuedAt = state.IssuedAt,
                    DueAt = state.DueAt,
                    AmountCents = state.AmountCents,
                    CorporationCount = state.CorporationCount,
                    PaidAt = state.PaidAt,
                    PaidByCorporationId = state.PaidByCorporationId,
                    DivisionKey = state.DivisionKey,
                    Renewed = state.Renewed,
                },
                ProjectAllianceBill,
                "Id,AllianceId,IssuedAt,DueAt,AmountCents,CorporationCount,PaidAt,PaidByCorporationId,DivisionKey,Renewed",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        reader.GetInt32(9),
                        reader.GetBoolean(10)
                    ),
                state => new(state.Id),
                indexes: [new("AllianceId", row => row.AllianceId), new("Renewed", row => row.Renewed)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectAllianceBill(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAllianceBill>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAllianceBill WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAllianceBill(
                    Id,AllianceId,IssuedAt,DueAt,AmountCents,CorporationCount,PaidAt,PaidByCorporationId,DivisionKey,Renewed)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.IssuedAt'),
                    json_extract(value,'$.DueAt'),
                    json_extract(value,'$.AmountCents'),
                    json_extract(value,'$.CorporationCount'),
                    json_extract(value,'$.PaidAt'),
                    json_extract(value,'$.PaidByCorporationId'),
                    json_extract(value,'$.DivisionKey'),
                    json_extract(value,'$.Renewed')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    AllianceId=excluded.AllianceId,
                    IssuedAt=excluded.IssuedAt,
                    DueAt=excluded.DueAt,
                    AmountCents=excluded.AmountCents,
                    CorporationCount=excluded.CorporationCount,
                    PaidAt=excluded.PaidAt,
                    PaidByCorporationId=excluded.PaidByCorporationId,
                    DivisionKey=excluded.DivisionKey,
                    Renewed=excluded.Renewed
                """
            );
        }
    }
}
