using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyCorporationVote(long Id, int CorporationId, long StartedAt, long EndsAt, string DataJson);

internal sealed partial class ResidentEconomy
{
    private void RegisterCorporationVoting() =>
        Register(
            new EconomyTable<SavedCorporationVote, EconomyCorporationVote>(
                "WorldCorporationVote",
                row => new(row.Id),
                row => new(row.Id, row.CorporationId, row.StartedAt, row.EndsAt, row.DataJson),
                state => new SavedCorporationVote
                {
                    Id = state.Id,
                    CorporationId = state.CorporationId,
                    StartedAt = state.StartedAt,
                    EndsAt = state.EndsAt,
                    DataJson = state.DataJson,
                },
                ProjectCorporationVoting,
                "Id,CorporationId,StartedAt,EndsAt,DataJson",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5)),
                state => new(state.Id),
                indexes: [new("CorporationId", row => row.CorporationId)]
            )
        );

    private static void ProjectCorporationVoting(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyCorporationVote>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationVote WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationVote(Id,CorporationId,StartedAt,EndsAt,DataJson)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.CorporationId'),json_extract(value,'$.StartedAt'),
                    json_extract(value,'$.EndsAt'),json_extract(value,'$.DataJson') FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET CorporationId=excluded.CorporationId,StartedAt=excluded.StartedAt,
                    EndsAt=excluded.EndsAt,DataJson=excluded.DataJson
                """
            );
        }
    }
}
