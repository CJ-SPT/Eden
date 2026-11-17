using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyCapitalChange(long Id, int AllianceId, long At, int? SystemId);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyCapitalChange() =>
        Register(
            new EconomyTable<SavedSovereigntyCapitalChange, EconomySovereigntyCapitalChange>(
                "WorldSovereigntyCapitalChange",
                row => new(row.Id),
                row => new(row.Id, row.AllianceId, row.At, row.SystemId),
                state => new SavedSovereigntyCapitalChange
                {
                    Id = state.Id,
                    AllianceId = state.AllianceId,
                    At = state.At,
                    SystemId = state.SystemId,
                },
                ProjectSovereigntyCapitalChange,
                "Id,AllianceId,At,SystemId",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt32(4)),
                state => new(state.Id),
                indexes: [new("AllianceId", row => row.AllianceId)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectSovereigntyCapitalChange(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySovereigntyCapitalChange>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyCapitalChange WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyCapitalChange(
                    Id,
                    AllianceId,
                    At,
                    SystemId)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.At'),
                    json_extract(value,'$.SystemId')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    AllianceId=excluded.AllianceId,
                    At=excluded.At,
                    SystemId=excluded.SystemId
                """
            );
        }
    }
}
