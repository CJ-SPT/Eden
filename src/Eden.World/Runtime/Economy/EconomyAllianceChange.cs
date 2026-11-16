using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyAllianceChange(long Id, int AllianceId, int CorporationId, string DataJson);

internal sealed partial class ResidentEconomy
{
    private void RegisterAllianceChange() =>
        Register(
            new EconomyTable<SavedAllianceChange, EconomyAllianceChange>(
                "WorldAllianceChange",
                row => new(row.Id),
                row => new(row.Id, row.AllianceId, row.CorporationId, row.DataJson),
                state => new SavedAllianceChange
                {
                    Id = state.Id,
                    AllianceId = state.AllianceId,
                    CorporationId = state.CorporationId,
                    DataJson = state.DataJson,
                },
                ProjectAllianceChange,
                "Id,AllianceId,CorporationId,DataJson",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4)),
                state => new(state.Id),
                indexes: [new("AllianceId", row => row.AllianceId), new("CorporationId", row => row.CorporationId)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectAllianceChange(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAllianceChange>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAllianceChange WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAllianceChange(Id,AllianceId,CorporationId,DataJson)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.DataJson')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    AllianceId=excluded.AllianceId,
                    CorporationId=excluded.CorporationId,
                    DataJson=excluded.DataJson
                """
            );
        }
    }
}
