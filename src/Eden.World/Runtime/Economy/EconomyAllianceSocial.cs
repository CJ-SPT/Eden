using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyAllianceSocial(int AllianceId, long Revision, string DataJson);

internal sealed partial class ResidentEconomy
{
    private void RegisterAllianceSocial() =>
        Register(
            new EconomyTable<SavedAllianceSocial, EconomyAllianceSocial>(
                "WorldAllianceSocial",
                row => new(row.AllianceId),
                row => new(row.AllianceId, row.Revision, row.DataJson),
                state => new SavedAllianceSocial
                {
                    AllianceId = state.AllianceId,
                    Revision = state.Revision,
                    DataJson = state.DataJson,
                },
                ProjectAllianceSocial,
                "AllianceId,Revision,DataJson",
                reader => new(reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3)),
                state => new(state.AllianceId),
                indexes: [],
                primaryColumn: "AllianceId"
            )
        );

    private static void ProjectAllianceSocial(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAllianceSocial>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAllianceSocial WHERE AllianceId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAllianceSocial(AllianceId,Revision,DataJson)
                SELECT
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.Revision'),
                    json_extract(value,'$.DataJson')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(AllianceId) DO UPDATE SET
                    Revision=excluded.Revision,
                    DataJson=excluded.DataJson
                """
            );
        }
    }
}
