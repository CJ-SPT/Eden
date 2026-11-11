using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCorporation(
    int Id,
    long BalanceCents,
    long CeoId,
    long CreatedAt,
    long CreatorId,
    string DetailsJson,
    int MemberLimit,
    string Name,
    int StationId,
    int SystemId,
    string Ticker
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCorporation(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCorporation>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCorporation
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCorporation.Id=json_extract(value,'$.Id')
                    )
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldCorporation(
                        Id,BalanceCents,CeoId,CreatedAt,CreatorId,DetailsJson,MemberLimit,Name,StationId,SystemId,Ticker
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.BalanceCents'),json_extract(value,'$.CeoId'),
                        json_extract(value,'$.CreatedAt'),json_extract(value,'$.CreatorId'),
                        json_extract(value,'$.DetailsJson'),json_extract(value,'$.MemberLimit'),
                        json_extract(value,'$.Name'),json_extract(value,'$.StationId'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.Ticker')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        BalanceCents=excluded.BalanceCents,CeoId=excluded.CeoId,CreatedAt=excluded.CreatedAt,
                        CreatorId=excluded.CreatorId,DetailsJson=excluded.DetailsJson,MemberLimit=excluded.MemberLimit,
                        Name=excluded.Name,StationId=excluded.StationId,SystemId=excluded.SystemId,Ticker=excluded.Ticker
                    """
                );
            }
        }
    }
}
