using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyCorporationRecruitmentAd(long Id, int CorporationId, long CreatedAt, long ExpiresAt, string DetailsJson);

internal sealed partial class ResidentEconomy
{
    private void RegisterCorporationRecruitment() =>
        Register(
            new EconomyTable<SavedCorporationRecruitmentAd, EconomyCorporationRecruitmentAd>(
                "WorldCorporationRecruitmentAd",
                row => new(row.Id),
                row => new(row.Id, row.CorporationId, row.CreatedAt, row.ExpiresAt, row.DetailsJson),
                state => new SavedCorporationRecruitmentAd
                {
                    Id = state.Id,
                    CorporationId = state.CorporationId,
                    CreatedAt = state.CreatedAt,
                    ExpiresAt = state.ExpiresAt,
                    DetailsJson = state.DetailsJson,
                },
                ProjectCorporationRecruitment,
                "Id,CorporationId,CreatedAt,ExpiresAt,DetailsJson",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5)),
                state => new(state.Id),
                indexes: [new("CorporationId", row => row.CorporationId)]
            )
        );

    private static void ProjectCorporationRecruitment(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationRecruitmentAd>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationRecruitmentAd WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationRecruitmentAd(Id,CorporationId,CreatedAt,ExpiresAt,DetailsJson)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.CorporationId'),json_extract(value,'$.CreatedAt'),
                    json_extract(value,'$.ExpiresAt'),json_extract(value,'$.DetailsJson')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET CorporationId=excluded.CorporationId,CreatedAt=excluded.CreatedAt,
                    ExpiresAt=excluded.ExpiresAt,DetailsJson=excluded.DetailsJson
                """
            );
        }
    }
}
